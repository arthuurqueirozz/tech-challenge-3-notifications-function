param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d{12}$')][string]$AccountId,
    [string]$Profile = 'fiap-fase3',
    [switch]$TestDependencyFailure
)
$ErrorActionPreference = 'Stop'
$awsCommand = Get-Command aws -ErrorAction SilentlyContinue
$awsExe = if ($awsCommand) { $awsCommand.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\Amazon\AWSCLIV2\aws.exe' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('fcg-cloud-smoke-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$utf8 = New-Object Text.UTF8Encoding($false)
$functionName = 'fcg-fase3-notifications'
$table = 'fcg-fase3-notification-events'
$started = [DateTimeOffset]::UtcNow
$checks = New-Object 'System.Collections.Generic.List[object]'

function Invoke-AwsJson([string]$Service, [string]$Operation, [hashtable]$InputData, [string[]]$Extra = @()) {
    $inputPath = Join-Path $scratch 'request.json'
    [IO.File]::WriteAllText($inputPath, ($InputData | ConvertTo-Json -Depth 30 -Compress), $utf8)
    $raw = & $awsExe $Service $Operation --cli-input-json ('file://' + $inputPath) --profile $Profile --region us-east-1 --no-cli-pager --output json @Extra
    if ($LASTEXITCODE -ne 0) { throw "AWS $Service $Operation failed." }
    if ($raw) { return (($raw -join "`n") | ConvertFrom-Json) }
}
function Assert-True([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw "FAIL: $Description" }
    Write-Host "PASS: $Description"
    $checks.Add(@{ check = $Description; atUtc = [DateTimeOffset]::UtcNow.ToString('O') })
}
function New-UserEvent {
    return @{ EventId = [guid]::NewGuid().ToString(); OccurredAtUtc = [DateTime]::UtcNow.ToString('O'); UserId = [guid]::NewGuid().ToString(); Name = 'Synthetic smoke'; Email = 'smoke@example.invalid' }
}
function Read-Item([string]$Key) {
    $result = Invoke-AwsJson dynamodb get-item @{ TableName = $table; Key = @{ EventKey = @{ S = $Key } }; ConsistentRead = $true }
    return $result.Item
}
function Wait-Item([string]$Key, [int]$Seconds = 120) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $item = Read-Item $Key
        if ($item.ProcessingStatus.S -eq 'Completed') { return $item }
        Start-Sleep -Seconds 5
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timeout waiting for $Key"
}
function Send-Event([string]$Queue, [hashtable]$Event) {
    return Invoke-AwsJson sqs send-message @{ QueueUrl = $Queue; MessageBody = ($Event | ConvertTo-Json -Compress) }
}
function Read-Logs {
    # Host clock may be ahead of AWS; unique message IDs provide correlation within this wider window.
    $request = @{ logGroupName = '/aws/lambda/fcg-fase3-notifications'; startTime = $started.AddMinutes(-10).ToUnixTimeMilliseconds(); limit = 1000 }
    $all = New-Object 'System.Collections.Generic.List[object]'
    for ($page = 0; $page -lt 10; $page++) {
        $result = Invoke-AwsJson logs filter-log-events $request
        foreach ($entry in $result.events) { $all.Add($entry) }
        if (!$result.nextToken) { return $all.ToArray() }
        $request.nextToken = $result.nextToken
    }
    throw 'Logs exceeded smoke page limit; narrow the test window.'
}
function Wait-Log([string]$MessageId, [string]$Result, [int]$Seconds = 600) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $events = Read-Logs
        $found = @($events | Where-Object { $_.message.Contains($MessageId) -and $_.message.Contains('"result":"' + $Result + '"') })
        if ($found.Count -gt 0) { return $found }
        Start-Sleep -Seconds 10
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timeout waiting for log $MessageId/$Result"
}
function Wait-FunctionUpdate {
    for ($attempt = 0; $attempt -lt 24; $attempt++) {
        $configuration = Invoke-AwsJson lambda get-function-configuration @{ FunctionName = $functionName }
        if ($configuration.LastUpdateStatus -eq 'Successful') { return $configuration }
        if ($configuration.LastUpdateStatus -eq 'Failed') { throw 'Function configuration update failed.' }
        Start-Sleep -Seconds 5
    }
    throw 'Function configuration update timed out.'
}

$identity = Invoke-AwsJson sts get-caller-identity @{}
Assert-True ($identity.Account -eq $AccountId -and $identity.Arn.EndsWith(':user/fiap-fase3-cli')) 'Expected account and IAM deploy user'
$mappings = Invoke-AwsJson lambda list-event-source-mappings @{ FunctionName = $functionName }
Assert-True (@($mappings.EventSourceMappings).Count -eq 2 -and @($mappings.EventSourceMappings | Where-Object State -ne 'Enabled').Count -eq 0) 'Both SQS mappings enabled'
$userUrl = (Invoke-AwsJson sqs get-queue-url @{ QueueName = 'fcg-fase3-user-created' }).QueueUrl
$paymentUrl = (Invoke-AwsJson sqs get-queue-url @{ QueueName = 'fcg-fase3-payment-processed' }).QueueUrl
$userArn = "arn:aws:sqs:us-east-1:${AccountId}:fcg-fase3-user-created"

$welcome = New-UserEvent
$welcomeKey = 'user-created:' + $welcome.EventId
$welcomeMessage = Send-Event $userUrl $welcome
$welcomeItem = Wait-Item $welcomeKey
Assert-True ($welcomeItem.NotificationKind.S -eq 'Welcome' -and $welcomeItem.NotificationState.S -eq 'Simulated') 'SQS welcome recorded in real DynamoDB'
[void](Wait-Log $welcomeMessage.MessageId 'SimulationRecorded')

$payment = @{ OrderId = [guid]::NewGuid().ToString(); ProcessedAtUtc = [DateTime]::UtcNow.ToString('O'); UserId = $welcome.UserId; UserEmail = 'smoke@example.invalid'; GameId = [guid]::NewGuid().ToString(); Price = 19.90; Status = 'Approved' }
$paymentKey = 'payment-processed:' + $payment.OrderId
$paymentMessage = Send-Event $paymentUrl $payment
$paymentItem = Wait-Item $paymentKey
Assert-True ($paymentItem.NotificationKind.S -eq 'PurchaseConfirmation' -and $paymentItem.NotificationState.S -eq 'Simulated') 'SQS approved payment recorded in real DynamoDB'
[void](Wait-Log $paymentMessage.MessageId 'SimulationRecorded')
$rejected = $payment.Clone()
$rejected.OrderId = [guid]::NewGuid().ToString()
$rejected.Status = 'Rejected'
[void](Send-Event $paymentUrl $rejected)
$rejectedItem = Wait-Item ('payment-processed:' + $rejected.OrderId)
Assert-True ($rejectedItem.NotificationKind.S -eq 'None' -and $rejectedItem.NotificationState.S -eq 'Skipped') 'Rejected payment skips confirmation'
Assert-True (@($welcomeItem.Data.M.PSObject.Properties).Count -lt @($paymentItem.Data.M.PSObject.Properties).Count) 'Flexible event data has distinct user and payment fields'

$duplicateWelcome = Send-Event $userUrl $welcome
$payment.ProcessedAtUtc = [DateTime]::UtcNow.AddSeconds(1).ToString('O')
$duplicatePayment = Send-Event $paymentUrl $payment
[void](Wait-Log $duplicateWelcome.MessageId 'Duplicate')
[void](Wait-Log $duplicatePayment.MessageId 'Duplicate')
Assert-True ((Read-Item $welcomeKey).ProcessedAtUtc.S -eq $welcomeItem.ProcessedAtUtc.S) 'User redelivery leaves original simulation unchanged'
Assert-True ((Read-Item $paymentKey).ProcessedAtUtc.S -eq $paymentItem.ProcessedAtUtc.S) 'Payment redelivery with changed timestamp leaves simulation unchanged'

# Direct invocation deterministically tests a mixed batch; the queue triggers were tested above.
$batchUser = New-UserEvent
$validId = [guid]::NewGuid().ToString()
$invalidId = [guid]::NewGuid().ToString()
$batch = @{ Records = @(
    @{ messageId = $validId; eventSourceARN = $userArn; eventSource = 'aws:sqs'; body = ($batchUser | ConvertTo-Json -Compress) },
    @{ messageId = $invalidId; eventSourceARN = $userArn; eventSource = 'aws:sqs'; body = '{invalid-json' }
) }
$payloadPath = Join-Path $scratch 'batch.json'
$responsePath = Join-Path $scratch 'response.json'
[IO.File]::WriteAllText($payloadPath, ($batch | ConvertTo-Json -Depth 10 -Compress), $utf8)
$invokeOutput = & $awsExe lambda invoke --function-name $functionName --payload ('fileb://' + $payloadPath) --profile $Profile --region us-east-1 --no-cli-pager --output json $responsePath
if ($LASTEXITCODE -ne 0) { throw 'AWS lambda invoke failed.' }
$invocation = ($invokeOutput -join "`n") | ConvertFrom-Json
Assert-True (!$invocation.FunctionError) 'Mixed batch invocation completes normally'
$response = Get-Content -Raw -Encoding UTF8 $responsePath | ConvertFrom-Json
Assert-True (@($response.batchItemFailures).Count -eq 1 -and $response.batchItemFailures[0].itemIdentifier -eq $invalidId) 'Only invalid record returned for retry in mixed batch'
[void](Wait-Item ('user-created:' + $batchUser.EventId))

if ($TestDependencyFailure) {
    # Isolated test: point at a table outside the role's permission, then restore the original environment.
    $original = Invoke-AwsJson lambda get-function-configuration @{ FunctionName = $functionName }
    Assert-True ($original.Environment.Variables.EVENTS_TABLE -eq $table) 'Expected table configuration before dependency failure'
    $originalVariables = @{}
    foreach ($property in $original.Environment.Variables.PSObject.Properties) { $originalVariables[$property.Name] = $property.Value }
    $deniedVariables = $originalVariables.Clone()
    $deniedVariables.EVENTS_TABLE = $table + '-smoke-denied'
    $deniedEvent = New-UserEvent
    $deniedKey = 'user-created:' + $deniedEvent.EventId
    try {
        [void](Invoke-AwsJson lambda update-function-configuration @{ FunctionName = $functionName; RevisionId = $original.RevisionId; Environment = @{ Variables = $deniedVariables } })
        $changed = Wait-FunctionUpdate
        Assert-True ($changed.Environment.Variables.EVENTS_TABLE -eq $deniedVariables.EVENTS_TABLE) 'Dependency failure configuration applied'
        $deniedMessage = Send-Event $userUrl $deniedEvent
        [void](Wait-Log $deniedMessage.MessageId 'Retry')
        Assert-True (!(Read-Item $deniedKey)) 'Real dependency denial did not mark event completed'
    }
    finally {
        $current = Wait-FunctionUpdate
        if ($current.Environment.Variables.EVENTS_TABLE -eq $deniedVariables.EVENTS_TABLE) {
            [void](Invoke-AwsJson lambda update-function-configuration @{ FunctionName = $functionName; RevisionId = $current.RevisionId; Environment = @{ Variables = $originalVariables } })
            [void](Wait-FunctionUpdate)
        }
        elseif ($current.Environment.Variables.EVENTS_TABLE -ne $table) { throw 'Unexpected concurrent environment change; inspect the function before continuing.' }
        Write-Host 'Original DynamoDB table configuration restored.'
    }
    # Same message retries automatically after the configured 180-second visibility timeout.
    [void](Wait-Item $deniedKey 300)
    [void](Wait-Log $deniedMessage.MessageId 'SimulationRecorded')
    Assert-True ($true) 'Same SQS message recovered automatically after dependency access restored'
    $after = Invoke-AwsJson lambda get-function-configuration @{ FunctionName = $functionName }
    Assert-True ($after.Environment.Variables.EVENTS_TABLE -eq $table) 'Original table configuration confirmed'
}

$logs = Read-Logs
Assert-True (@($logs | Where-Object { $_.message.Contains('smoke@example.invalid') -or $_.message.Contains('Synthetic smoke') }).Count -eq 0) 'CloudWatch logs omit synthetic name and email'
foreach ($queueUrl in @($userUrl, $paymentUrl)) {
    $drained = $false
    for ($attempt = 0; $attempt -lt 18; $attempt++) {
        $attributes = (Invoke-AwsJson sqs get-queue-attributes @{ QueueUrl = $queueUrl; AttributeNames = @('ApproximateNumberOfMessages', 'ApproximateNumberOfMessagesNotVisible') }).Attributes
        if ([int]$attributes.ApproximateNumberOfMessages -eq 0 -and [int]$attributes.ApproximateNumberOfMessagesNotVisible -eq 0) { $drained = $true; break }
        Start-Sleep -Seconds 5
    }
    Assert-True $drained 'SQS queue drained after test'
}
$evidence = @{ startedUtc = $started.ToString('O'); finishedUtc = [DateTimeOffset]::UtcNow.ToString('O'); region = 'us-east-1'; function = $functionName; dependencyFailureTest = [bool]$TestDependencyFailure; checks = @($checks.ToArray()); eventKeys = @($welcomeKey, $paymentKey, ('payment-processed:' + $rejected.OrderId)); messages = @($welcomeMessage.MessageId, $paymentMessage.MessageId); logs = @($logs | ForEach-Object { @{ timestamp = $_.timestamp; message = $_.message } }) }
$evidencePath = Join-Path $PSScriptRoot '../cloud-smoke.local.json'
[IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 20), $utf8)
Write-Host "Evidence: $evidencePath"
Write-Host 'Pause mappings through SAM NotificationsEnabled=false after the session.'
