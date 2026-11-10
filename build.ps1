# Construit l'installeur : dist\Temperator-Setup-<version>.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.2.3]
# Nécessite le SDK .NET 10 (winget install Microsoft.DotNet.SDK.10).
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$proj = 'src\Temperator\Temperator.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version invalide : '$Version' (attendu : 1.2.3)" }

$art = Join-Path $PSScriptRoot 'artifacts'
$app = Join-Path $art 'app'
$payload = Join-Path $art 'payload'
$dist = Join-Path $PSScriptRoot 'dist'
Remove-Item $art, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app, $payload, $dist | Out-Null

Write-Host "Temperator $Version" -ForegroundColor Cyan

Write-Host '1/2  Application'
dotnet publish $proj -c Release -o $app -p:Version=$Version --nologo -v quiet
if ($LASTEXITCODE) { throw 'Échec de la compilation de Temperator.' }
Copy-Item LICENSE, LICENCES-TIERS.txt $app
Get-ChildItem $app -Filter *.pdb | Remove-Item
Compress-Archive -Path "$app\*" -DestinationPath (Join-Path $payload 'Temperator.zip') -CompressionLevel Optimal

Write-Host '2/2  Temperator-Setup.exe (installeur en C#, contient l''application)'
dotnet build src\Setup\Setup.csproj -c Release -o (Join-Path $art 'setup') -p:Version=$Version "-p:PayloadDir=$payload" --nologo -v quiet
if ($LASTEXITCODE) { throw "Échec de la compilation de l'installeur." }
$exe = Join-Path $dist "Temperator-Setup-$Version.exe"
Copy-Item (Join-Path $art 'setup\Temperator-Setup.exe') $exe

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $exe -Leaf)" | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
Write-Host ("OK  {0}  ({1:N1} Mo)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
Write-Host "SHA-256 $hash"
Write-Host 'Pour une release : joins Temperator-Setup-*.exe ET SHA256SUMS.txt (la mise à jour automatique vérifie cette empreinte).'

# ------------------------------------------------------------------ manifestes winget (à soumettre après la release GitHub)
$id = 'Buntasam.Temperator'
$wingetDir = Join-Path $dist "winget\manifests\b\Buntasam\Temperator\$Version"
New-Item -ItemType Directory -Force $wingetDir | Out-Null
$url = "https://github.com/Buntasam/Temperator/releases/download/v$Version/Temperator-Setup-$Version.exe"
$utf8 = New-Object Text.UTF8Encoding $false
function Write-Manifest($name, $text) { [IO.File]::WriteAllText((Join-Path $wingetDir $name), $text.Replace("`r`n", "`n"), $utf8) }

Write-Manifest "$id.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: fr-FR
ManifestType: version
ManifestVersion: 1.6.0
"@

Write-Manifest "$id.installer.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
InstallerType: exe
Scope: machine
InstallModes:
- interactive
- silent
- silentWithProgress
InstallerSwitches:
  Silent: /S
  SilentWithProgress: /S
UpgradeBehavior: install
ElevationRequirement: elevatesSelf
ProductCode: Temperator
AppsAndFeaturesEntries:
- DisplayName: Temperator
  Publisher: Temperator
  ProductCode: Temperator
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $($hash.ToUpperInvariant())
ManifestType: installer
ManifestVersion: 1.6.0
"@

Write-Manifest "$id.locale.fr-FR.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: fr-FR
Publisher: Buntasam
PublisherUrl: https://github.com/Buntasam
PublisherSupportUrl: https://github.com/Buntasam/Temperator/issues
PackageName: Temperator
PackageUrl: https://github.com/Buntasam/Temperator
License: Propriétaire (code visible, usage personnel gratuit)
LicenseUrl: https://github.com/Buntasam/Temperator/blob/main/LICENSE
ShortDescription: Les températures et l'utilisation de ton PC, toujours à l'écran.
Description: Un widget discret qui affiche la température et l'utilisation du processeur, de la carte graphique, de la mémoire et des disques, avec alertes, historique et mode jeu.
Tags:
- temperature
- monitoring
- cpu
- gpu
- widget
- hardware
ReleaseNotesUrl: https://github.com/Buntasam/Temperator/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@

Write-Manifest "$id.locale.en-US.yaml" @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Buntasam
PublisherUrl: https://github.com/Buntasam
PublisherSupportUrl: https://github.com/Buntasam/Temperator/issues
PackageName: Temperator
PackageUrl: https://github.com/Buntasam/Temperator
License: Proprietary (source-available, free for personal use)
LicenseUrl: https://github.com/Buntasam/Temperator/blob/main/LICENSE
ShortDescription: Your PC's temperatures and usage, always on screen.
Description: A discreet widget showing the temperature and load of the processor, graphics card, memory and drives, with alerts, history and a game mode.
Tags:
- temperature
- monitoring
- cpu
- gpu
- widget
- hardware
ReleaseNotesUrl: https://github.com/Buntasam/Temperator/releases/tag/v$Version
ManifestType: locale
ManifestVersion: 1.6.0
"@
Write-Host "winget : $wingetDir"

