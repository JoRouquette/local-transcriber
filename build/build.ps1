<#
.SYNOPSIS
    Build complet : moteur Python gele + publication .NET + packaging Velopack.
.DESCRIPTION
    1. Gele le moteur Python (build-engine.ps1) sauf si -SkipEngine.
    2. Publie la GUI, le service et le serveur MCP (win-x64, self-contained).
    3. Copie le moteur gele dans le dossier de publication (engine\).
    4. Cree un installeur Velopack (necessite l'outil vpk : dotnet tool install -g vpk).
.EXAMPLE
    .\build.ps1 -Version 0.1.0
    .\build.ps1 -Version 0.1.0 -Cuda
#>
param(
    [string]$Version = "0.1.0",
    [switch]$Cuda,
    [switch]$SkipEngine
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "build\publish"
$releases = Join-Path $root "build\Releases"

if (-not $SkipEngine) {
    Write-Host "==> Gel du moteur Python" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "build-engine.ps1") -Cuda:$Cuda
}

Write-Host "==> Publication .NET (win-x64, self-contained)" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "/p:PublishSingleFile=false", "/p:Version=$Version", "-o", $publish)

# Le serveur MCP est desormais heberge par le service (HTTP) : la lib Mcp est tiree
# automatiquement par LocalTranscriber.Service, pas de publication separee.
dotnet publish (Join-Path $root "src\LocalTranscriber.Gui\LocalTranscriber.Gui.csproj") @common
dotnet publish (Join-Path $root "src\LocalTranscriber.Service\LocalTranscriber.Service.csproj") @common

Write-Host "==> Copie du moteur gele" -ForegroundColor Cyan
$engineSrc = Join-Path $root "engine\dist\transcriber-engine"
$engineDst = Join-Path $publish "engine"
if (-not (Test-Path $engineSrc)) { throw "Moteur gele introuvable. Lancez build-engine.ps1 d'abord." }
Copy-Item $engineSrc $engineDst -Recurse -Force

Write-Host "==> Packaging Velopack" -ForegroundColor Cyan
# Necessite : dotnet tool install -g vpk
# Velopack cree par defaut UN raccourci Menu Demarrer (pas de raccourci Bureau) = choix voulu.
$icon = Join-Path $root "src\LocalTranscriber.Gui\Assets\app.ico"
vpk pack `
    --packId "LocalTranscriber" `
    --packTitle "LocalTranscriber" `
    --packVersion $Version `
    --packDir $publish `
    --mainExe "LocalTranscriber.exe" `
    --icon $icon `
    --outputDir $releases

Write-Host "OK -> installeur dans $releases" -ForegroundColor Green
