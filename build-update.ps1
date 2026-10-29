<#
.SYNOPSIS
  Construit une livraison Utils.Colas pour le serveur IIS non accessible directement.
.DESCRIPTION
  Produit dans <OutputDir> deux fichiers à copier-coller sur le serveur :
    UPDyyyyMMdd-HHmm.zip           publication complète (dossier site\) + manifest.json
    Install-UPDyyyyMMdd-HHmm.ps1   script d'installation (empreinte SHA-256 du zip intégrée)
  Étapes : contrôle Git (arbre propre) -> tests -> dotnet publish -> zip -> script d'installation.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-update.ps1
  powershell -ExecutionPolicy Bypass -File .\build-update.ps1 -SkipTests -AllowDirty   # dépannage uniquement
#>
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot 'livraisons'),
    [switch]$SkipTests,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$projectDir = $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path $dotnet) { $env:DOTNET_ROOT = Split-Path $dotnet } else { $dotnet = 'dotnet' }

$id = 'UPD' + (Get-Date -Format 'yyyyMMdd-HHmm')
$zipPath = Join-Path $OutputDir "$id.zip"
$installPath = Join-Path $OutputDir "Install-$id.ps1"
if (Test-Path $zipPath) { throw "La livraison $id existe déjà : $zipPath" }

# 1. Traçabilité : la livraison doit correspondre à un commit
Write-Host "[1/5] Contrôle Git..." -ForegroundColor Cyan
$commit = (git -C $projectDir rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'git rev-parse a échoué' }
$dirty = git -C $projectDir status --porcelain
if ($dirty) {
    if (-not $AllowDirty) { throw "Modifications non commitées : commiter avant de livrer (ou -AllowDirty).`n$($dirty -join "`n")" }
    Write-Warning 'Arbre Git modifié : la livraison ne correspond pas exactement au commit.'
    $commit += '-dirty'
}

# 2. Tests
if (-not $SkipTests) {
    Write-Host "[2/5] Tests..." -ForegroundColor Cyan
    & $dotnet test (Join-Path $projectDir 'Utils.Colas.slnx') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests en échec : livraison annulée.' }
}
else { Write-Warning '[2/5] Tests ignorés (-SkipTests).' }

# 3. Publication (hors de l'arborescence du projet pour ne pas polluer le globbing du SDK Web)
Write-Host "[3/5] Publication..." -ForegroundColor Cyan
$stage = Join-Path $env:TEMP "Utils.Colas-$id"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$siteDir = Join-Path $stage 'site'
& $dotnet publish (Join-Path $projectDir 'Utils.Colas.csproj') -c Release -o $siteDir --no-self-contained --nologo `
    "-p:InformationalVersion=$id+$commit" -p:IncludeSourceRevisionInInformationalVersion=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish a échoué' }

$leaked = Get-ChildItem $siteDir -Recurse -File -Include 'secrets.json', 'appsettings.Local.json'
if ($leaked) { throw "Fichier sensible présent dans la publication : $($leaked.FullName -join ', ')" }
foreach ($bin in 'ObjectBrowser.exe', 'PWCommon.dll') {
    if (-not (Test-Path (Join-Path $siteDir "Tools\ObjectBrowser\$bin"))) {
        Write-Warning "$bin absent de la livraison : le serveur conservera sa version actuelle."
    }
}

$manifest = [ordered]@{
    id      = $id
    commit  = $commit
    built   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    builtBy = "$env:USERDOMAIN\$env:USERNAME"
    files   = (Get-ChildItem $siteDir -Recurse -File).Count
}
$manifest | ConvertTo-Json | Set-Content (Join-Path $stage 'manifest.json') -Encoding UTF8

# 4. Zip
Write-Host "[4/5] Archive $id.zip..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
# Entrées créées une à une : sous Windows PowerShell 5.1, CreateFromDirectory écrit des séparateurs '\' non standard.
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $entry = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash
Remove-Item $stage -Recurse -Force

# 5. Script d'installation (UTF-8 avec BOM : requis par Windows PowerShell 5.1 pour les accents)
Write-Host "[5/5] Script Install-$id.ps1..." -ForegroundColor Cyan
$template = [IO.File]::ReadAllText((Join-Path $projectDir 'Deploy\Install-Update.template.ps1'))
$script = $template.Replace('__UPD_ID__', $id).Replace('__ZIP_SHA256__', $sha).Replace('__COMMIT__', $commit)
[IO.File]::WriteAllText($installPath, $script, (New-Object System.Text.UTF8Encoding($true)))

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ''
Write-Host "Livraison prête ($sizeMb Mo, commit $commit) :" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host "  $installPath"
Write-Host 'Sur le serveur : copier les 2 fichiers dans un même dossier, puis en administrateur :'
Write-Host "  powershell -ExecutionPolicy Bypass -File .\Install-$id.ps1"
