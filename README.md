# FIAP Cloud Games - Notifications Function

Função .NET 10 para receber `UserCreatedEvent` e `PaymentProcessedEvent` diretamente
de duas filas SQS Standard. Substitui o host ASP.NET Core contínuo da NotificationsAPI.
A Fase 4 preserva os handlers e os contratos da Fase 3, atualiza o runtime para
.NET 10 e parametriza uma instalação nova na conta/região autorizadas.
A validação local e as provas AWS têm evidências separadas no repositório central.

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

## Recursos SAM da Fase 4

`template.yaml` contém uma Lambda 256 MB/.NET 10/x86_64, duas filas SQS Standard,
uma tabela DynamoDB on-demand, a execution role da função e um log group com
retenção de 7 dias. `ProjectName` tem padrão `fcg-fase4`; os nomes dos recursos
são derivados desse prefixo. Use stack nova para esta instalação, preservando a
stack e os recursos históricos da Fase 3.

SAM administra somente a identidade da função. As identidades de Users/Payments
pertencem a `infra/aws/templates/runtime-identity.json` da orquestração e usam
EKS Pod Identity com `sqs:SendMessage` exclusivamente na fila de cada produtor.
A função usa sua execution role para consumir as duas filas, gravar/ler a tabela
e registrar logs. Nenhum usuário IAM ou credencial temporária do host é injetado
nos pods ou na função.

O parâmetro `NotificationsEnabled` começa em `false`, permitindo preparar os
recursos antes de ativar os consumidores. Use `true` durante as provas dos dois
eventos e durante a apresentação. Fora dessa janela, uma pausa precisa ser
explícita; ela não congela a retenção das mensagens nem encerra todos os custos.
Passe ambos os parâmetros explicitamente nas atualizações para evitar preservar
um valor anterior involuntariamente.

| Output SAM | Uso na orquestração |
|---|---|
| `UserCreatedQueueArn`, `PaymentProcessedQueueArn` | Políticas de publicação e associação das identidades dos pods |
| `UserCreatedQueueUrl`, `PaymentProcessedQueueUrl` | Configuração SQS de Users/Payments, respectivamente |
| `EventsTableName`, `EventsTableArn` | Evidência NoSQL e inventário de retenção |
| `FunctionName`, `FunctionArn` | Inspeção da função e dos dois event source mappings |
| `LogGroupName`, `LogGroupArn` | Consulta/exportação de logs seguros |
| `NotificationsRoleArn` | Inspeção das permissões de execução da função |

Não existem outputs de papéis de produtores no SAM. A sequência é SAM → runtime
identity da orquestração → bootstrap dos produtores, usando os outputs reais.
O bucket privado de artefatos é preparação externa do SAM, com bloqueio de acesso
público e criptografia; seu nome e sua conta proprietária devem ser verificados
antes de uso. Não use recursos ou credenciais da conta anterior por suposição.

## Testes e build local

Na raiz deste repositório, com SDK .NET 10 e SAM CLI disponíveis:

```powershell
dotnet restore TechChallenge.Notifications.Function.sln --locked-mode
dotnet build TechChallenge.Notifications.Function.sln --configuration Release --no-restore
dotnet run --project tests/FCG.Notifications.Function.Tests/FCG.Notifications.Function.Tests.csproj --configuration Release
sam validate --lint --template-file template.yaml --region us-east-2
sam build --template-file template.yaml
```

Os testes existentes usam um cliente DynamoDB em memória para exercitar o handler
e o store: dois eventos, aprovação/rejeição, duplicidade, timestamp de retry,
falha antes/depois da gravação, leitura, lote parcial, payload inválido,
origem desconhecida, conflito, privacidade e cancelamento. Eles não comprovam
SQS/Lambda/DynamoDB reais. Mudança apenas no template não exige repetir os testes
do handler quando sua fonte e suas dependências permanecem iguais.

## Instalação e prova AWS

A região acordada da Fase 4 é `us-east-2`, perfil local `fiap-fase4`, stack nova
`fcg-fase4-notifications`. A janela autorizada é 13/11/2026 20h até 18/11/2026
20h BRT; criação depende do gate de conta FREE/ACTIVE, saldo, permissões e cobertura
de custo do runbook central. Sem upgrade para Paid ou desembolso pessoal.

O [README central da orquestração](https://github.com/arthuurqueirozz/tech-challenge-3-orchestration)
coordena o bucket de artefatos, build/deploy SAM e a passagem de outputs entre stacks.
Antes de deploy, conferir conta, região, parâmetros e changeset. A publicação
deve usar o template gerado pelo `sam build`, `CAPABILITY_NAMED_IAM` e o bucket
privado efetivamente preparado. O build local não cria recursos AWS.

Após instalar e ativar `NotificationsEnabled=true`, comprovar:

1. Os dois mappings estão habilitados e apontam para os ARNs reais do SAM.
2. Cadastro via Users publica `UserCreatedEvent` e cria o item correspondente no DynamoDB.
3. Pagamento aprovado/rejeitado via Payments cria o item e o estado esperados.
4. Repetição com o mesmo identificador preserva um único efeito lógico.
5. Logs e itens de evidência não contêm nome, e-mail, token, senha ou payload completo.

Registre conta/região, versão, horários, IDs sintéticos, estados observados e
caminhos das provas; não inclua segredos. A simulação não envia e-mail real.

`scripts/cloud-smoke.ps1`, `scripts/render-deploy-policy.ps1` e `iam/` preservam a
implementação histórica F3 com nomes, identidade e política antigos. Eles não são
o procedimento cloud da Fase 4. O teste antigo `-TestDependencyFailure` altera o
ambiente da função e não deve ser usado na instância final concorrente. A prova
F4 segue o procedimento não destrutivo da orquestração.

## Retenção e encerramento

As duas filas, a tabela e o log group têm `DeletionPolicy: Retain` e
`UpdateReplacePolicy: Retain`. Excluir a stack remove função/papel/mappings, mas
preserva esses quatro recursos; trocar o prefixo também deixa os dados anteriores
retidos. Recursos preservados podem continuar gerando cobrança e não são
automaticamente adotados por uma nova stack com o mesmo nome.

Na janela de encerramento autorizada, interromper os produtores, drenar e conferir
as filas, pausar os dois mappings e exportar os dados/evidências acordados. Depois
da remoção da stack, inventariar explicitamente filas/tabela/log group e o bucket
de artefatos, tratando os recursos retidos conforme a autorização de preservação.
Pausar consumers não impede expiração SQS após quatro dias nem expiração de logs
após sete dias. Não prometer preservação indefinida com essas retenções.

## Referências

- [SQS/Lambda](https://docs.aws.amazon.com/lambda/latest/dg/services-sqs-configure.html)
- [Falhas parciais](https://docs.aws.amazon.com/lambda/latest/dg/services-sqs-errorhandling.html)
- [Runtimes Lambda](https://docs.aws.amazon.com/lambda/latest/dg/lambda-runtimes.html)
- [Pod Identity e trust por namespace/ServiceAccount](https://docs.aws.amazon.com/eks/latest/userguide/pod-id-role.html)
