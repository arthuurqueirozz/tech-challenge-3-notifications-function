[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{12}$')]
    [string]$AccountId
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$policyTemplate = Join-Path $repoRoot 'iam/deploy-policy.template.json'
$policyOutput = Join-Path $repoRoot 'iam/deploy-policy.local.json'
$policyText = (Get-Content -LiteralPath $policyTemplate -Raw).Replace('ACCOUNT_ID', $AccountId)
$null = $policyText | ConvertFrom-Json
[System.IO.File]::WriteAllText($policyOutput, $policyText, [System.Text.UTF8Encoding]::new($false))
Write-Output $policyOutput
