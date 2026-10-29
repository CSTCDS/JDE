#Requires -Version 5.1
<#
.SYNOPSIS
  Installe la livraison __UPD_ID__ de Utils.Colas (commit __COMMIT__) sur le serveur IIS.
.DESCRIPTION
  Copier __UPD_ID__.zip et ce script dans un même dossier local du serveur (ex. C:\Livraisons\Utils.Colas),
  puis clic droit sur le script > « Exécuter avec PowerShell » (élévation UAC demandée automatiquement),
  ou en console : powershell -ExecutionPolicy Bypass -File .\Install-__UPD_ID__.ps1

  1. Contrôles : empreinte SHA-256 du zip, Hosting Bundle .NET 10, site IIS pointant sur -SitePath
  2. Arrêt du site et du pool
  3. Sauvegarde <SitePath>.bak-<horodatage> (les -KeepBackups plus récentes sont conservées)
  4. Publication en miroir ; conservés sur le serveur : secrets.json, appsettings.Production.json,
     keys\, logs\ et binaires ObjectBrowser (sauf s'ils sont fournis dans la livraison)
  5. Redémarrage du pool et du site
  6. Contrôle /health ; en cas d'échec, retour automatique à la sauvegarde

  Journal : <SitePath>\logs\deploy\ (transcription, robocopy, history.log)
.EXAMPLE
  .\Install-__UPD_ID__.ps1
  .\Install-__UPD_ID__.ps1 -HealthUrl https://utils.colas.local/health
  .\Install-__UPD_ID__.ps1 -Rollback                     # restaure la sauvegarde la plus récente
  .\Install-__UPD_ID__.ps1 -Rollback -BackupPath C:\sites\JDE.bak-20261029-171000
#>
param(
    [string]$SitePath = 'C:\sites\JDE',
    [string]$ZipPath = (Join-Path $PSScriptRoot '__UPD_ID__.zip'),
    [int]$KeepBackups = 5,
    [string]$HealthUrl = '',
    [switch]$Rollback,
    [string]$BackupPath = '',
    [switch]$NoPause
)

# Lancement par clic droit « Exécuter avec PowerShell » : session non élevée -> relance en administrateur (UAC).
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    foreach ($key in $PSBoundParameters.Keys) {
        $value = $PSBoundParameters[$key]
        if ($value -is [switch]) { if ($value.IsPresent) { $argList += "-$key" } }
        else { $argList += "-$key"; $argList += "`"$value`"" }
    }
    try {
        Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -WorkingDirectory $PSScriptRoot
    }
    catch {
        Write-Host "Droits administrateur requis : élévation refusée ou impossible ($($_.Exception.Message))." -ForegroundColor Red
        if (-not $NoPause) { [void](Read-Host 'Appuyer sur Entrée pour fermer') }
    }
    return
}

$ErrorActionPreference = 'Stop'
$UpdId = '__UPD_ID__'
$ExpectedSha256 = '__ZIP_SHA256__'
$Commit = '__COMMIT__'

$PreserveFiles = @('secrets.json', 'appsettings.Production.json', 'ObjectBrowser.exe', 'PWCommon.dll', 'app_offline.htm')
$PreserveDirs = @('logs', 'keys')
$SitePath = [IO.Path]::GetFullPath($SitePath).TrimEnd('\')
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$LogDir = Join-Path $SitePath 'logs\deploy'

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Write-Step([string]$Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }

function Get-TargetSite {
    $found = @(Get-Website | Where-Object {
        [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($_.physicalPath)).TrimEnd('\') -ieq $SitePath
    })
    if ($found.Count -eq 0) { throw "Aucun site IIS ne pointe sur $SitePath." }
    if ($found.Count -gt 1) { throw "Plusieurs sites IIS pointent sur $SitePath : $($found.name -join ', ')." }
    $found[0]
}

function Test-PoolShared([string]$Pool, [string]$SiteName) {
    $others = @(Get-Website | Where-Object { $_.applicationPool -eq $Pool -and $_.name -ne $SiteName })
    $others += @(Get-WebApplication | Where-Object { $_.applicationPool -eq $Pool })
    $others.Count -gt 0
}

function Stop-Target($Site) {
    $pool = $Site.applicationPool
    if ((Get-Website -Name $Site.name).State -eq 'Started') { Stop-Website -Name $Site.name }
    if (Test-PoolShared $pool $Site.name) {
        # Pool partagé : on ne l'arrête pas ; app_offline.htm décharge uniquement cette application (ANCM).
        Write-Warning "Pool '$pool' partagé avec d'autres sites : déchargement via app_offline.htm."
        Set-Content (Join-Path $SitePath 'app_offline.htm') '<html><body>Mise à jour en cours...</body></html>' -Encoding UTF8
        Start-Sleep -Seconds 5
        return
    }
    if ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') { Stop-WebAppPool -Name $pool }
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') {
        if ((Get-Date) -gt $deadline) { throw "Le pool '$pool' ne s'arrête pas." }
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 2   # libération des verrous fichiers par w3wp
}

function Start-Target($Site) {
    Remove-Item (Join-Path $SitePath 'app_offline.htm') -ErrorAction SilentlyContinue
    if ((Get-WebAppPoolState -Name $Site.applicationPool).Value -ne 'Started') { Start-WebAppPool -Name $Site.applicationPool }
    if ((Get-Website -Name $Site.name).State -ne 'Started') { Start-Website -Name $Site.name }
}

function Invoke-Robocopy([string]$From, [string]$To, [string[]]$ExtraArgs) {
    $log = Join-Path $LogDir "$UpdId-$Stamp-robocopy.log"
    & robocopy.exe $From $To /MIR /R:3 /W:5 /NP /NDL "/LOG+:$log" @ExtraArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy $From -> $To a échoué (code $LASTEXITCODE, voir $log)" }
}

function Get-HealthUrl($Site) {
    if ($HealthUrl) { return $HealthUrl }
    $scheme = 'https'
    $b = Get-WebBinding -Name $Site.name -Protocol https | Select-Object -First 1
    if (-not $b) { $scheme = 'http'; $b = Get-WebBinding -Name $Site.name -Protocol http | Select-Object -First 1 }
    $parts = $b.bindingInformation.Split(':')   # ip:port:hôte
    $hostName = $parts[2]
    if (-not $hostName) { $hostName = [Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName }
    "${scheme}://${hostName}:$($parts[1])/health"
}

# 'ok', 'ko' (l'application répond en erreur) ou 'indetermine' (TLS, DNS, 401 IIS, ...).
function Test-Health([string]$Url) {
    $last = ''
    for ($i = 1; $i -le 10; $i++) {
        try {
            $r = Invoke-WebRequest $Url -UseBasicParsing -UseDefaultCredentials -TimeoutSec 30
            if ($r.StatusCode -eq 200) { Write-Host "  /health : 200 $($r.Content)" -ForegroundColor Green; return 'ok' }
            $last = "HTTP $($r.StatusCode)"
        }
        catch {
            $resp = $_.Exception.Response
            if ($resp) { $last = "HTTP $([int]$resp.StatusCode)" } else { $last = $_.Exception.Message }
        }
        Write-Host "  tentative $i : $last"
        Start-Sleep -Seconds 3
    }
    if ($last -match '^HTTP (5\d\d|404)') { return 'ko' }
    Write-Warning "Contrôle /health non concluant ($last)."
    'indetermine'
}

function Confirm-Health($Site) {
    $url = Get-HealthUrl $Site
    Write-Host "  URL : $url"
    $state = Test-Health $url
    if ($state -eq 'indetermine') {
        $answer = Read-Host "Ouvrir $url dans un navigateur. Le site fonctionne-t-il ? (O/N)"
        if ($answer -match '^[oOyY]') { $state = 'ok' } else { $state = 'ko' }
    }
    $state -eq 'ok'
}

function Restore-Backup($Site, [string]$From) {
    Write-Step "Retour arrière depuis $From"
    Stop-Target $Site
    Invoke-Robocopy $From $SitePath (@('/XF') + $PreserveFiles + @('/XD') + $PreserveDirs)
    Start-Target $Site
}

function Add-History([string]$Result) {
    $line = '{0};{1};{2};{3}\{4};{5}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $UpdId, $Commit, $env:USERDOMAIN, $env:USERNAME, $Result
    try { Add-Content (Join-Path $LogDir 'history.log') $line -Encoding UTF8 } catch { Write-Warning "history.log non mis à jour : $($_.Exception.Message)" }
}

function Get-Backups {
    Get-ChildItem (Split-Path $SitePath) -Directory -Filter "$(Split-Path $SitePath -Leaf).bak-*" | Sort-Object Name -Descending
}

$staging = "$SitePath.staging-$UpdId"
$backup = ''
$stopped = $false
$copyStarted = $false
$failed = $false
$transcript = $false
$site = $null

try {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    Start-Transcript -Path (Join-Path $LogDir "$UpdId-$Stamp-install.log") | Out-Null
    $transcript = $true
    Import-Module WebAdministration
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

    $site = Get-TargetSite
    Write-Host "Site IIS : $($site.name) | pool : $($site.applicationPool) | dossier : $SitePath"

    # ---------- Retour arrière manuel ----------
    if ($Rollback) {
        if (-not $BackupPath) { $BackupPath = Get-Backups | Select-Object -First 1 -ExpandProperty FullName }
        if (-not $BackupPath -or -not (Test-Path $BackupPath)) { throw 'Aucune sauvegarde trouvée.' }
        if ((Read-Host "Restaurer $BackupPath sur $SitePath ? (O/N)") -notmatch '^[oOyY]') { throw 'Annulé.' }
        Restore-Backup $site $BackupPath
        if (Confirm-Health $site) { Add-History "ROLLBACK OK ($BackupPath)"; Write-Host 'Retour arrière terminé.' -ForegroundColor Green }
        else { Add-History "ROLLBACK KO ($BackupPath)"; throw 'Le site ne répond pas correctement après retour arrière.' }
        $global:LASTEXITCODE = 0   # robocopy laisse 1..7 (succès) : code de sortie du script = 0
        return
    }

    # ---------- 1. Contrôles (rien n'est arrêté tant qu'ils ne sont pas passés) ----------
    Write-Step "1/6 Contrôles $UpdId (commit $Commit)"
    if (-not (Test-Path $ZipPath)) { throw "Archive introuvable : $ZipPath" }
    Unblock-File $ZipPath
    $sha = (Get-FileHash $ZipPath -Algorithm SHA256).Hash
    if ($sha -ne $ExpectedSha256) { throw "Empreinte SHA-256 incorrecte (copie corrompue ?) : $sha au lieu de $ExpectedSha256" }
    Write-Host '  Empreinte SHA-256 : OK'

    $aspnet = Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.AspNetCore.App" -Directory -Filter '10.*' -ErrorAction SilentlyContinue
    if (-not $aspnet) { throw 'Runtime ASP.NET Core 10 absent : installer le Hosting Bundle .NET 10 (puis iisreset) et relancer.' }
    if (-not (Test-Path "$env:windir\System32\inetsrv\aspnetcorev2.dll")) { throw 'Module IIS AspNetCoreModuleV2 absent : installer le Hosting Bundle .NET 10.' }
    Write-Host "  ASP.NET Core : $($aspnet.Name -join ', ')"

    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    [IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $staging)
    $manifest = Get-Content (Join-Path $staging 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.id -ne $UpdId) { throw "Le zip contient la livraison $($manifest.id), pas $UpdId." }
    $source = Join-Path $staging 'site'
    foreach ($f in 'Utils.Colas.dll', 'web.config') {
        if (-not (Test-Path (Join-Path $source $f))) { throw "Livraison incomplète : $f absent." }
    }
    Write-Host "  Livraison : $($manifest.files) fichiers, construite le $($manifest.built) par $($manifest.builtBy)"

    # ---------- 2. Arrêt ----------
    Write-Step '2/6 Arrêt du site'
    $stopped = $true
    Stop-Target $site

    # ---------- 3. Sauvegarde ----------
    Write-Step '3/6 Sauvegarde'
    if (Get-ChildItem $SitePath -Force | Where-Object { $_.Name -ne 'logs' -and $_.Name -ne 'app_offline.htm' }) {
        $backup = "$SitePath.bak-$Stamp"
        Invoke-Robocopy $SitePath $backup @('/XD', 'logs', '/XF', 'app_offline.htm')
        Write-Host "  $backup"
        Get-Backups | Select-Object -Skip $KeepBackups |
            ForEach-Object { Write-Host "  Suppression de l'ancienne sauvegarde $($_.Name)"; Remove-Item $_.FullName -Recurse -Force }
    }
    else { Write-Host '  Première installation : rien à sauvegarder.' }

    # ---------- 4. Publication ----------
    Write-Step '4/6 Publication'
    $copyStarted = $true
    Invoke-Robocopy $source $SitePath (@('/XF') + $PreserveFiles + @('/XD') + $PreserveDirs)
    foreach ($bin in 'ObjectBrowser.exe', 'PWCommon.dll') {
        $src = Join-Path $source "Tools\ObjectBrowser\$bin"
        $dst = Join-Path $SitePath "Tools\ObjectBrowser\$bin"
        if (Test-Path $src) { Copy-Item $src $dst -Force }
        elseif (-not (Test-Path $dst)) { Write-Warning "$bin absent : la comparaison ObjectBrowser de JDE Compare ne fonctionnera pas." }
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $SitePath 'keys') | Out-Null
    if (-not (Test-Path (Join-Path $SitePath 'secrets.json'))) {
        Copy-Item (Join-Path $source 'secrets.template.json') (Join-Path $SitePath 'secrets.json')
        Write-Warning 'secrets.json créé depuis le modèle : renseigner les identifiants puis chiffrer (Tools\encrypt-secrets.ps1 -Machine).'
    }

    # Droits de l'identité du pool + authentification Windows imposée
    $pm = (Get-Item "IIS:\AppPools\$($site.applicationPool)").processModel
    switch ($pm.identityType) {
        'ApplicationPoolIdentity' { $identity = "IIS AppPool\$($site.applicationPool)" }
        'SpecificUser'            { $identity = $pm.userName }
        'NetworkService'          { $identity = 'NT AUTHORITY\NETWORK SERVICE' }
        'LocalService'            { $identity = 'NT AUTHORITY\LOCAL SERVICE' }
        default                   { $identity = 'NT AUTHORITY\SYSTEM' }
    }
    & icacls.exe $SitePath /grant "${identity}:(OI)(CI)RX" /Q | Out-Null
    foreach ($d in 'logs', 'keys') { & icacls.exe (Join-Path $SitePath $d) /grant "${identity}:(OI)(CI)M" /Q | Out-Null }
    try {
        Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $site.name -Filter /system.webServer/security/authentication/windowsAuthentication -Name enabled -Value $true
        Set-WebConfigurationProperty -PSPath 'IIS:\' -Location $site.name -Filter /system.webServer/security/authentication/anonymousAuthentication -Name enabled -Value $false
    }
    catch { Write-Warning "Impossible d'imposer l'authentification Windows : $($_.Exception.Message)" }

    # ---------- 5. Redémarrage ----------
    Write-Step '5/6 Redémarrage'
    Start-Target $site

    # ---------- 6. Vérification ----------
    Write-Step '6/6 Vérification'
    if (-not (Confirm-Health $site)) { throw 'Le site ne répond pas correctement après installation.' }

    Add-History 'OK'
    Write-Host "`nLivraison $UpdId installée." -ForegroundColor Green
    Write-Host 'Checklist : menu affiché avec votre utilisateur et la livraison en pied de page de JDE Compare,'
    Write-Host '            Lire .h lit un objet, JDE Compare charge les statuts, aucun Ctrl+F5 nécessaire.'
}
catch {
    $err = $_
    Write-Host "`nÉCHEC : $($err.Exception.Message)" -ForegroundColor Red
    if ($copyStarted -and $backup) {
        try { Restore-Backup $site $backup; Add-History "KO - restauré depuis $backup ($($err.Exception.Message))" }
        catch { Add-History "KO - RETOUR ARRIÈRE EN ÉCHEC ($($_.Exception.Message))"; Write-Host "Retour arrière en échec : $($_.Exception.Message)" -ForegroundColor Red }
    }
    elseif ($copyStarted) { Add-History "KO - première installation, pas de sauvegarde ($($err.Exception.Message))" }
    elseif ($stopped) {
        try { Start-Target $site } catch { }
        Add-History "KO - aucune modification ($($err.Exception.Message))"
    }
    elseif (-not $Rollback) { Add-History "KO - aucune modification ($($err.Exception.Message))" }
    $failed = $true
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
    if ($transcript) { Stop-Transcript | Out-Null }
    # Garde la fenêtre ouverte (lancement par clic droit) pour lire le résultat.
    if (-not $NoPause) { [void](Read-Host "`nAppuyer sur Entrée pour fermer") }
}
if ($failed) { exit 1 } else { exit 0 }
