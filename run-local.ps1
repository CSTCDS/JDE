<#
.SYNOPSIS
  Lance Utils.Colas en local (HTTPS, Windows Authentication via Negotiate).
.EXAMPLE
  .\run-local.ps1            # https://localhost:6070
  .\run-local.ps1 -Test      # exécute les tests puis quitte
#>
param([switch]$Test)

$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
# SDK installé dans le profil utilisateur (dotnet-install.ps1) si absent de Program Files
$userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path $userDotnet) { $dotnet = $userDotnet; $env:DOTNET_ROOT = Split-Path $userDotnet }
if (-not $dotnet) { throw 'SDK .NET 10 introuvable (https://dot.net).' }

Set-Location $PSScriptRoot

if ($Test) {
    & $dotnet test .\tests\Utils.Colas.Tests\Utils.Colas.Tests.csproj
    exit $LASTEXITCODE
}

if (-not (Test-Path .\secrets.json)) {
    Write-Warning 'secrets.json absent : copier secrets.template.json en secrets.json et renseigner les identifiants Oracle.'
}

# Certificat de développement HTTPS (une seule fois)
& $dotnet dev-certs https --check --trust *> $null
if ($LASTEXITCODE -ne 0) { & $dotnet dev-certs https --trust }

& $dotnet run --launch-profile https
