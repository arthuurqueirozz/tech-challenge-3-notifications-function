# FIAP Cloud Games - Notifications Function

Função .NET 8 para receber `UserCreatedEvent` e `PaymentProcessedEvent` diretamente
de duas filas SQS Standard. Substitui o host ASP.NET Core contínuo da NotificationsAPI.
Implementação local validada por 18 testes, lint e build SAM em 2026-09-14;
o deploy AWS e os gates das Etapas 2/3 ainda estão pendentes.

## Origem e contratos

Contratos copiados sem alterar schemas ou namespace `FCG.IntegrationEvents.V1` de
[NotificationsAPI da Fase 2](https://github.com/leo-bernar/tech-challenge-2-notifications-api),
commit `f97a5dd16b706bfcab95328fec2185fab5c83ca2`. Código do grupo reutilizado
com autorização do responsável. O histórico da origem continua no clone separado.
As regras dos consumers foram preservadas: cadastro simula boas-vindas; pagamento
aprovado simula confirmação; rejeitado não envia confirmação.

Corpos SQS são JSON direto dos contratos (PascalCase/camelCase aceitos), sem envelope
MassTransit. O ARN da fila determina o tipo, não campos arbitrários do payload.
Usuários mantêm EventId; pagamentos usam OrderId como identificador estável.
Status desconhecido e payload inválido falham com retry; isso torna explícito um erro
que o consumer antigo apenas registrava. Não há API HTTP, Swagger, Dockerfile,
Deployment ou envio real de e-mail nesta função.

## Simulação, NoSQL e idempotência

O efeito do simulador é um item durável no DynamoDB: `NotificationState=Simulated`
para boas-vindas/confirmação ou `Skipped` para pagamento rejeitado.
Uma escrita condicional `attribute_not_exists(EventKey)` grava atomicamente
o evento, sua simulação e `ProcessingStatus=Completed`. Não se grava uma reserva
antes de um efeito externo; não existe envio externo entre gravações.

Chaves: `user-created:{EventId}` e `payment-processed:{OrderId}`.
O mapa flexível `Data` contém campos distintos por evento. Não guarda nome,
e-mail, senha, token ou corpo completo. Timestamps, tipo, estado, chave e fingerprint
permitem correlacionar o evento. O timestamp de pagamento não entra no fingerprint,
pois o produtor herdado pode recalculá-lo durante retry.

Em duplicidade, a função lê com consistência forte e só reconhece o evento se ele
já estiver completo e semanticamente igual. Itens incompletos/conflitantes falham.
Falha antes da escrita permite nova tentativa; resposta perdida após o commit
é resolvida pela mesma leitura, sem outra simulação.
Os registros permanecem até remoção explícita da stack/tabela; não há TTL que
possa apagar a chave antes de um redelivery tardio.

CloudWatch recebe um log estruturado com resultado, chave e ID da mensagem.
Ele é uma projeção de diagnóstico do registro, não um segundo envio de e-mail:
uma falha após o commit pode deixar apenas o log de duplicidade no retry.
A garantia demonstrável é uma simulação durável por chave enquanto o item existir,
não entrega exatamente uma vez de efeitos externos. Adicionar SMTP/SES exigiria
reavaliar essas garantias e está fora do escopo.

## Retry e limites

`ReportBatchItemFailures` reconhece apenas as mensagens processadas com sucesso.
Mensagens inválidas, falhas de dependência, conflito ou cancelamento retornam seus
IDs para retry; outras mensagens continuam a ser processadas. Logs de falha não
incluem payload nem texto de exceções que possa conter dados sensíveis.

SQS tem visibility timeout de 180s (seis vezes o timeout Lambda de 30s),
retenção de 4 dias e long polling de 20s. Duas execuções simultâneas por trigger.
Sem DLQ adicional para manter as duas filas escolhidas no plano: uma mensagem
permanentemente inválida repete e deve ser diagnosticada antes da retenção expirar.
Não há promessa de armazenamento infinito ou de recuperação após expiração SQS.

## Recursos SAM

`template.yaml`: uma Lambda 256 MB/.NET 8/x86_64, duas filas SQS Standard,
uma tabela DynamoDB on-demand, execution role e log group com retenção de 7 dias.
Sem VPC, NAT, provisioned concurrency ou Kubernetes na AWS.
Nomes são fixos `fcg-fase3-*`: uma instalação por conta/região.
O bucket S3 de artefatos de deploy é suporte do SAM e será preparado à parte.

Runtime .NET 8 confirmado como suportado em 2026-09-13. A documentação AWS prevê
depreciação em 2026-11-10; verificar novamente na data do deploy, sem migrar
o framework silenciosamente.

## Testes e build

Na raiz deste repositório, com SDK .NET 8 disponível:

```powershell
dotnet run --project tests/FCG.Notifications.Function.Tests/FCG.Notifications.Function.Tests.csproj --configuration Release
sam validate --lint --template-file template.yaml --region us-east-1
sam build --template-file template.yaml
```

Os testes usam um cliente DynamoDB em memória para exercitar o handler e o store.
Cobrem os dois eventos, aprovação/rejeição, duplicidade, timestamp de retry,
falha antes/depois da gravação, falha de leitura, lote parcial, payload inválido,
origem desconhecida, item incompleto/conflitante, privacidade e cancelamento.
Eles não são evidência de SQS/Lambda/DynamoDB reais.

## Deploy e acesso

Perfil local `fiap-fase3`, região `us-east-1`. A chave IAM fica no mecanismo local
da AWS CLI; nunca no Git nem nas variáveis da Lambda, que usa a execution role.
O usuário responsável precisa anexar as [permissões de deploy](iam/README.md)
antes da execução. O gerador da política já foi validado localmente e não acessa a AWS.
Custo/créditos precisam ser conferidos frente ao teto de USD 1/mês.

Depois de validar o template, preparar o bucket de artefatos e as permissões:

```powershell
sam deploy --template-file .aws-sam/build/template.yaml --stack-name fcg-fase3-notifications --s3-bucket SEU_BUCKET_DE_ARTEFATOS --capabilities CAPABILITY_NAMED_IAM --region us-east-1 --profile fiap-fase3 --confirm-changeset
aws cloudformation describe-stacks --stack-name fcg-fase3-notifications --profile fiap-fase3 --region us-east-1 --query "Stacks[0].Outputs"
aws logs tail /aws/lambda/fcg-fase3-notifications --since 10m --profile fiap-fase3 --region us-east-1
```

Substituir o bucket pelo nome real; não executar com placeholder.
Publicação real dos dois eventos, observação dos logs e consulta da tabela precisam
ser realizadas antes de marcar os gates cloud. Produtores serão adaptados na Etapa 4.

## Remoção

Depois de preservar evidências, conferir stack, conta, região e autorização:
`sam delete --stack-name fcg-fase3-notifications --profile fiap-fase3 --region us-east-1`.
A remoção da stack apaga os registros da tabela e logs (sem políticas de retenção
de recursos), além das filas e função. O bucket de artefatos exige verificação
separada. Não executar limpeza enquanto as evidências ainda forem necessárias.

## Referências

- Enunciado local, páginas 4 a 6; plano de implementação, Etapas 2 e 3.
- [SQS/Lambda](https://docs.aws.amazon.com/lambda/latest/dg/services-sqs-configure.html)
- [Falhas parciais](https://docs.aws.amazon.com/lambda/latest/dg/services-sqs-errorhandling.html)
- [Runtimes](https://docs.aws.amazon.com/lambda/latest/dg/lambda-runtimes.html)
