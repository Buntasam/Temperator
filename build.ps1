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

Write-Host '1/3  Application'
dotnet publish $proj -c Release -o $app -p:Version=$Version --nologo -v quiet
if ($LASTEXITCODE) { throw 'Échec de la compilation de Temperator.' }
Copy-Item LICENCES-TIERS.txt $app
Get-ChildItem $app -Filter *.pdb | Remove-Item
Compress-Archive -Path "$app\*" -DestinationPath (Join-Path $payload 'Temperator.zip') -CompressionLevel Optimal

Write-Host '2/3  Installeur'
$script = Get-Content installer\Installer.ps1 -Raw -Encoding UTF8
$script = $script -replace "(?m)^\`$Version = '[^']*'", "`$`$Version = '$Version'"
[IO.File]::WriteAllText((Join-Path $payload 'Installer.ps1'), $script, (New-Object Text.UTF8Encoding $true))   # BOM : accents corrects sous PowerShell 5.1

Write-Host '3/3  Temperator-Setup.exe'
dotnet build src\Setup\Setup.csproj -c Release -o (Join-Path $art 'setup') -p:Version=$Version "-p:PayloadDir=$payload" --nologo -v quiet
if ($LASTEXITCODE) { throw "Échec de la compilation de l'installeur." }
$exe = Join-Path $dist "Temperator-Setup-$Version.exe"
Copy-Item (Join-Path $art 'setup\Temperator-Setup.exe') $exe

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $exe -Leaf)" | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
Write-Host ("OK  {0}  ({1:N1} Mo)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green
Write-Host "SHA-256 $hash"
