#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Publie Utils.Colas et crée/met à jour le site IIS (pool dédié, Windows Authentication, HTTPS).
.EXAMPLE
  .\deploy-iis.ps1 -SitePath C:\inetpub\Utils.Colas -Port 443 -HostName utils.colas -CertThumbprint <empreinte>
  .\deploy-iis.ps1 -SitePath C:\inetpub\Utils.Colas -SkipPublish
.NOTES
  Rollback : le contenu précédent est sauvegardé dans <SitePath>.bak-<horodatage> ;
  pour revenir en arrière, arrêter le pool, recopier la sauvegarde (robocopy /MIR) et redémarrer le pool.
#>
param(
    [Parameter(Mandatory)][string]$SitePath,
    [int]$Port = 443,
    [string]$HostName = '',
    [string]$CertThumbprint = '',
    [string]$SiteName = 'Utils.Colas',
    [string]$AppPoolName = 'Utils.Colas',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

$projectDir = $PSScriptRoot
$publishDir = Join-Path $projectDir 'publish'

# 1. Publication
if (-not $SkipPublish) {
    Write-Host '[1/6] dotnet publish...' -ForegroundColor Cyan
    dotnet publish "$projectDir\Utils.Colas.csproj" -c Release -o $publishDir --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a échoué' }
}
elseif (-not (Test-Path $publishDir)) { throw "Dossier publish introuvable : $publishDir" }

# 2. Application pool (sans code managé)
Write-Host '[2/6] Application pool...' -ForegroundColor Cyan
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) { New-WebAppPool -Name $AppPoolName | Out-Null }
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value 'AlwaysRunning'

# 3. Sauvegarde + copie (secrets.json, keys, logs et ObjectBrowser de la cible conservés)
Write-Host '[3/6] Sauvegarde et copie...' -ForegroundColor Cyan
$siteExists = Test-Path "IIS:\Sites\$SiteName"
if ($siteExists -and (Get-WebAppPoolState $AppPoolName).Value -eq 'Started') {
    Stop-WebAppPool $AppPoolName
    Start-Sleep -Seconds 2
}
if (Test-Path $SitePath) {
    $backup = "$SitePath.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    robocopy $SitePath $backup /MIR /XD logs /R:2 /W:3 /NFL /NDL /NJH /NJS | Out-Null
    Write-Host "  Sauvegarde : $backup"
}
New-Item -ItemType Directory -Force -Path $SitePath, "$SitePath\logs", "$SitePath\keys" | Out-Null
robocopy $publishDir $SitePath /MIR /XF secrets.json ObjectBrowser.exe PWCommon.dll /XD logs keys /R:2 /W:3 /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy a échoué (code $LASTEXITCODE)" }

$obSource = Join-Path $projectDir 'Tools\ObjectBrowser'
foreach ($bin in 'ObjectBrowser.exe', 'PWCommon.dll') {
    if (-not (Test-Path "$SitePath\Tools\ObjectBrowser\$bin") -and (Test-Path "$obSource\$bin")) {
        Copy-Item "$obSource\$bin" "$SitePath\Tools\ObjectBrowser\$bin"
    }
}
if (-not (Test-Path "$SitePath\secrets.json")) {
    Copy-Item "$projectDir\secrets.template.json" "$SitePath\secrets.json"
    Write-Warning "secrets.json créé depuis le modèle : renseigner les identifiants puis chiffrer avec Tools\encrypt-secrets.ps1 -Machine"
}

# 4. Droits NTFS pour l'identité du pool
Write-Host '[4/6] Permissions...' -ForegroundColor Cyan
icacls $SitePath /grant "IIS AppPool\${AppPoolName}:(OI)(CI)RX" /T | Out-Null
icacls "$SitePath\logs" /grant "IIS AppPool\${AppPoolName}:(OI)(CI)M" | Out-Null
icacls "$SitePath\keys" /grant "IIS AppPool\${AppPoolName}:(OI)(CI)M" | Out-Null

# 5. Site IIS (HTTPS)
Write-Host '[5/6] Site IIS...' -ForegroundColor Cyan
if (-not $siteExists) {
    New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPoolName -Port $Port -HostHeader $HostName -Ssl | Out-Null
}
else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SitePath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
}
if ($CertThumbprint) {
    $binding = Get-WebBinding -Name $SiteName -Protocol https | Select-Object -First 1
    $binding.AddSslCertificate($CertThumbprint, 'My')
}

Set-WebConfigurationProperty -Filter /system.webServer/security/authentication/windowsAuthentication -Name enabled -Value $true -Location $SiteName -PSPath 'IIS:\'
Set-WebConfigurationProperty -Filter /system.webServer/security/authentication/anonymousAuthentication -Name enabled -Value $false -Location $SiteName -PSPath 'IIS:\'

if ((Get-WebAppPoolState $AppPoolName).Value -ne 'Started') { Start-WebAppPool $AppPoolName }
if ((Get-Website $SiteName).State -ne 'Started') { Start-Website $SiteName }

# 6. Vérification
Write-Host '[6/6] Vérification...' -ForegroundColor Cyan
$hostPart = if ($HostName) { $HostName } else { 'localhost' }
$url = "https://${hostPart}:$Port/"
try {
    $health = Invoke-WebRequest "${url}health" -UseBasicParsing -UseDefaultCredentials -TimeoutSec 30
    Write-Host "Health : $($health.StatusCode) $($health.Content)" -ForegroundColor Green
}
catch { Write-Warning "Health check KO : $($_.Exception.Message)" }
Write-Host "Terminé : $url" -ForegroundColor Green
