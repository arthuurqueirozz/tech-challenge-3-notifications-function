# FIAP Cloud Games - Notifications Function

Função .NET 8 para receber `UserCreatedEvent` e `PaymentProcessedEvent` diretamente
de duas filas SQS Standard. Substitui o host ASP.NET Core contínuo da NotificationsAPI.
Implementação local validada por 18 testes, lint e build SAM em 2026-09-14.
Stack AWS criada em us-east-1; resultados do gate cloud são registrados na
documentação de evidências da orquestração.

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
O parâmetro `NotificationsEnabled` começa em `false`: primeiro deploy deixa os
dois gatilhos pausados, evitando polling ocioso. Durante a demonstração/validação,
ambos devem estar habilitados. Pausar gatilhos não exclui mensagens; mensagens
ainda expiram após a retenção SQS de 4 dias e devem ser processadas antes disso.
Nomes são fixos `fcg-fase3-*`: uma instalação por conta/região.
O bucket S3 de artefatos de deploy é suporte do SAM e será preparado à parte.

Runtime .NET 8 confirmado como suportado no deploy de 2026-09-14. A documentação AWS prevê
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
O usuário responsável anexou as [permissões de deploy](iam/README.md), verificadas
na criação real da stack. O gerador da política não acessa a AWS.
O planejamento sem créditos está em `docs/CUSTOS.md` da orquestração.

Depois de validar o template, preparar o bucket de artefatos e as permissões:

O nome do bucket é `fcg-fase3-artifacts-SEU_ACCOUNT_ID-us-east-1`. Para uma conta
nova, verificar primeiro que esse bucket não existe, criá-lo em `us-east-1` e
aplicar o bloqueio público (a criptografia padrão S3 é SSE-S3/AES256):

```powershell
aws s3api head-bucket --bucket SEU_BUCKET_DE_ARTEFATOS --expected-bucket-owner SEU_ACCOUNT_ID --profile fiap-fase3 --region us-east-1
# Somente após confirmar Not Found e a conta esperada:
aws s3api create-bucket --bucket SEU_BUCKET_DE_ARTEFATOS --profile fiap-fase3 --region us-east-1
aws s3api put-public-access-block --bucket SEU_BUCKET_DE_ARTEFATOS --public-access-block-configuration 'BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true' --profile fiap-fase3 --region us-east-1
aws s3api get-bucket-encryption --bucket SEU_BUCKET_DE_ARTEFATOS --profile fiap-fase3 --region us-east-1
```

```powershell
sam deploy --template-file .aws-sam/build/template.yaml --stack-name fcg-fase3-notifications --s3-bucket SEU_BUCKET_DE_ARTEFATOS --capabilities CAPABILITY_NAMED_IAM --region us-east-1 --profile fiap-fase3 --parameter-overrides NotificationsEnabled=false --confirm-changeset
aws cloudformation describe-stacks --stack-name fcg-fase3-notifications --profile fiap-fase3 --region us-east-1 --query "Stacks[0].Outputs"
aws logs tail /aws/lambda/fcg-fase3-notifications --since 10m --profile fiap-fase3 --region us-east-1
```

Substituir o bucket pelo nome real; não executar com placeholder.
Para a sessão de testes, repita o comando `sam deploy` acrescentando
`--parameter-overrides NotificationsEnabled=true`. Ao terminar, repita com
`--parameter-overrides NotificationsEnabled=false` e confira no changeset que
somente os dois mappings serão pausados. Manter o parâmetro explícito em deploys
posteriores, pois CloudFormation pode preservar o valor anterior. Essa pausa
reduz polling; não é um bloqueio automático de cobrança da conta.

Créditos promocionais: nenhum, conforme informado pelo responsável em 2026-09-14.
Planejamento conservador: até 20h/mês com gatilhos ativos, até 1.000 invocações
(incluindo retries), 100 MB de logs e 100 MB de artefatos. Ver estimativa em
`docs/CUSTOS.md` no repositório de orquestração. Ampliar uso requer recalcular
frente ao teto de USD 1/mês antes de prosseguir.
Publicação real dos dois eventos, observação dos logs e consulta da tabela precisam
ser realizadas antes de marcar os gates cloud. Produtores serão adaptados na Etapa 4.

## Smoke cloud reproduzível

Com a stack pronta e ambos os mappings habilitados, execute no PowerShell:

```powershell
./scripts/cloud-smoke.ps1 -AccountId SEU_ACCOUNT_ID -Profile fiap-fase3 -TestDependencyFailure
```

Substitua o ID da conta. O script confere identidade e usa apenas eventos sintéticos.
Testa welcome, aprovação/rejeição, dados flexíveis, duplicidade pelas duas filas,
lote direto com um registro válido e outro inválido, logs sem nome/e-mail e filas
drenadas. A invocação direta verifica a resposta parcial de forma determinística;
o acionamento real por SQS é verificado separadamente para ambos os eventos.

`-TestDependencyFailure` aponta temporariamente `EVENTS_TABLE` da Lambda para
`fcg-fase3-notification-events-smoke-denied`, fora das permissões da execution role.
Nenhuma tabela extra é criada. O script aguarda a atualização ficar pronta,
publica um evento e demonstra falha real de acesso ao DynamoDB sem marcação
prematura de conclusão. Em `finally`, restaura as variáveis originais e aguarda
o retry automático da mesma mensagem. Usa RevisionId para detectar concorrência.
Não executar durante uso concorrente do projeto. Se o processo for encerrado
à força, conferir o ambiente da função e restaurar `EVENTS_TABLE` para
`fcg-fase3-notification-events` antes de retomar. Template e role não são alterados.
O caso demonstra recuperação após restaurar a configuração de dependência;
não simula uma indisponibilidade regional da AWS.

Os logs podem demorar até dez minutos; a consulta começa dez minutos antes do
relógio local para tolerar pequena diferença de horário e correlaciona IDs únicos.
Uma diferença de cerca de dois minutos foi observada neste PC durante o primeiro
ensaio. Saída local em `cloud-smoke.local.json`, ignorada pelo Git. Os itens
sintéticos permanecem como evidência até a limpeza explícita da stack/tabela.
Ao terminar, aplicar `NotificationsEnabled=false` e conferir os dois mappings
em `Disabled`. O script não pausa o ambiente automaticamente.

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
