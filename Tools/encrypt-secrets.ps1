<#
.SYNOPSIS
  Chiffre (DPAPI) les UserId / Password de secrets.json (et AzureOpenAI.ApiKey) au format ENC:<base64>.
.DESCRIPTION
  Le chiffrement est lié au compte Windows qui exécute le script (scope CurrentUser) :
  l'exécuter sous l'identité du pool IIS (ou utiliser -Machine pour un chiffrement lié au serveur).
  Même entropie que Lire .h / PackTool : les valeurs ENC: existantes restent compatibles.
.EXAMPLE
  .\Tools\encrypt-secrets.ps1 -Path C:\inetpub\Utils.Colas\secrets.json -Machine
#>
param(
    [string]$Path = (Join-Path $PSScriptRoot '..\secrets.json'),
    [switch]$Machine
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$entropy = [Text.Encoding]::UTF8.GetBytes('PackTool.OracleConnection.v1')
$scope = if ($Machine) { [Security.Cryptography.DataProtectionScope]::LocalMachine } else { [Security.Cryptography.DataProtectionScope]::CurrentUser }

$json = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($conn in $json.OracleConnections.PSObject.Properties) {
    $c = $conn.Value
    foreach ($field in 'UserId', 'Password') {
        $v = [string]$c.$field
        if ($v -and -not $v.StartsWith('ENC:')) {
            $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($v), $entropy, $scope)
            $c.$field = 'ENC:' + [Convert]::ToBase64String($bytes)
        }
    }
    if ($c.PSObject.Properties.Name -contains 'UseEncryption') { $c.UseEncryption = $true }
    else { $c | Add-Member -NotePropertyName UseEncryption -NotePropertyValue $true }
    Write-Host "Chiffré : $($conn.Name)"
}
$ai = $json.PSObject.Properties['AzureOpenAI']
if ($ai -and $ai.Value.ApiKey -and -not ([string]$ai.Value.ApiKey).StartsWith('ENC:')) {
    $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes([string]$ai.Value.ApiKey), $entropy, $scope)
    $ai.Value.ApiKey = 'ENC:' + [Convert]::ToBase64String($bytes)
    Write-Host "Chiffré : AzureOpenAI.ApiKey"
}
$json | ConvertTo-Json -Depth 10 | Set-Content $Path -Encoding UTF8
Write-Host "Terminé : $Path" -ForegroundColor Green
