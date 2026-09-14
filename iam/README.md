# Permissões do deploy

O perfil `fiap-fase3` autentica como o usuário IAM `fiap-fase3-cli`.
A política gerenciada `FCGFase3Deploy` foi anexada pelo responsável em 2026-09-14;
as consultas IAM/Free Tier e a criação da stack SAM funcionaram. As negações
iniciais de acesso foram resolvidas sem conceder AdministratorAccess.

## Política para revisão e aplicação pelo responsável

`deploy-policy.template.json` corresponde aos nomes de `template.yaml` e ao
bucket `fcg-fase3-artifacts-ACCOUNT_ID-us-east-1`. Substitui-se somente ACCOUNT_ID.
Para gerar o documento local, ignorado pelo Git:

```powershell
./scripts/render-deploy-policy.ps1 -AccountId SEU_ID_DE_12_DIGITOS
```

O responsável deve abrir IAM no console, ir a Políticas -> Criar política -> JSON,
colar o conteúdo de `iam/deploy-policy.local.json`, revisar os avisos do editor
e criar uma política gerenciada pelo cliente chamada `FCGFase3Deploy`.
Em Usuários -> `fiap-fase3-cli` -> Adicionar permissões -> Anexar políticas
diretamente, selecionar essa política. Não usar política inline de usuário:
o documento excede o limite agregado de 2.048 caracteres desse tipo.

A política permite criar, atualizar e remover somente a stack nomeada, função,
duas filas, tabela, logs e bucket de artefatos indicados, e enviar eventos de teste.
Permite criar/alterar a execution role nomeada e passá-la ao Lambda. Na Etapa 4,
o responsável atualizou a política para incluir também as duas roles nomeadas
`fcg-fase3-users-publisher-role` e `fcg-fase3-payments-publisher-role`.
Essa capacidade de editar a role deve ficar restrita ao responsável pelo deploy.
Cada role de produtor tem apenas SendMessage na própria fila. O SAM confia
explicitamente no usuário IAM `fiap-fase3-cli` da mesma conta para AssumeRole;
essa trust policy permite obter sessões temporárias sem criar outras access keys.
`scripts/prepare-stage4.ps1` da orquestração gera sessões de uma hora em arquivos
locais ignorados pelo Git. Containers não recebem o perfil/chave de deploy.

Exceções que usam Resource `*`: validação CloudFormation, leitura de mappings,
quota da Lambda, listagem de log groups e consulta de Free Tier. Alterações em
event source mappings também usam `*`, mas exigem `lambda:FunctionArn` igual à
função deste projeto. O transform AWS SAM possui sua própria permissão.
Não concede edição de usuários, criação de access keys, anexação de políticas
gerenciadas, EKS, EC2, RDS, VPC ou serviços de rede.

Política derivada do template e validada na criação real da stack e atualização
dos gatilhos. Outros caminhos, como a remoção completa, ainda não foram executados:
erros de autorização devem ser analisados e corrigidos com ações específicas.
O responsável anexou a política pelo console; o agente não anexou políticas ao usuário.

## Separação da execution role

A política inline da role da função está no SAM e permite apenas consumir
as duas filas, PutItem/GetItem na tabela e escrever no log group correspondente.
Ela não contém credenciais permanentes e não tem acesso administrativo ao deploy.

## Referências

- https://docs.aws.amazon.com/lambda/latest/dg/permissions-user-function.html
- https://docs.aws.amazon.com/AWSCloudFormation/latest/UserGuide/control-access-with-iam.html
- https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies_create-console.html
