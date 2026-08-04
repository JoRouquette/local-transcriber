<#
.SYNOPSIS
    Build de l'installeur LEGER (sans gel PyInstaller) + packaging Velopack.
.DESCRIPTION
    L'installeur ne contient QUE l'app .NET (petite) + la source du moteur Python + le
    binaire uv. L'environnement Python (torch + deps) est installe au PREMIER LANCEMENT
    par l'application (uv), dans %LOCALAPPDATA%. Resultat : asset de release largement
    sous la limite GitHub de 2 Go, et build rapide.
.EXAMPLE
    .\build.ps1 -Version 1.0.0
#>
param(
    [string]$Version = "0.1.0",
    [string]$ReleaseNotes,
    # Signature de code (optionnelle) : PFX encode en base64 + mot de passe.
    # Si absent, l'installeur n'est pas signe (build normal).
    [string]$PfxBase64,
    [string]$PfxPassword
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# --- uv (bootstrapper Python) : version epinglee + integrite verifiable (finding M8) ---
# On ne telecharge plus "latest" (non deterministe, non verifiable) mais une version
# figee, sur une URL versionnee, avec controle SHA256.
# Version figee de uv (derniere stable au 2026-08-04). Pour la faire evoluer :
# recupere la nouvelle version et le SHA256 de uv-x86_64-pc-windows-msvc.zip sur
# https://github.com/astral-sh/uv/releases puis mets a jour les deux constantes ci-dessous.
$UvVersion = "0.11.17"
# SHA256 de reference de uv-x86_64-pc-windows-msvc.zip pour $UvVersion (asset officiel GitHub).
# Si renseigne, le build echoue en cas de non-correspondance (integrite obligatoire).
$UvSha256 = '35fc29e03e62f3cda769bc12773f3cb70ce305d0d36c0d8bd0c117dd0b3fcd14'

# Repli sur les variables d'environnement (utilise par la CI via les secrets GitHub).
if (-not $PfxBase64)   { $PfxBase64   = $env:SIGNING_PFX_BASE64 }
if (-not $PfxPassword) { $PfxPassword = $env:SIGNING_PFX_PASSWORD }
$publish = Join-Path $root "build\publish"
$releases = Join-Path $root "build\Releases"

Write-Host "==> Publication .NET (win-x64, self-contained)" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "/p:Version=$Version", "-o", $publish)
dotnet publish (Join-Path $root "src\LocalTranscriber.Gui\LocalTranscriber.Gui.csproj") @common
dotnet publish (Join-Path $root "src\LocalTranscriber.Service\LocalTranscriber.Service.csproj") @common

Write-Host "==> Copie de la source du moteur Python (pas de gel)" -ForegroundColor Cyan
$engineDst = Join-Path $publish "engine"
New-Item -ItemType Directory -Force -Path $engineDst | Out-Null
Copy-Item (Join-Path $root "engine\transcriber_engine") (Join-Path $engineDst "transcriber_engine") -Recurse -Force
Copy-Item (Join-Path $root "engine\requirements.txt") $engineDst -Force
Copy-Item (Join-Path $root "engine\constraints-torch-cpu.txt") $engineDst -Force
Copy-Item (Join-Path $root "engine\pyproject.toml") $engineDst -Force
Get-ChildItem $engineDst -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> Telechargement de uv $UvVersion (bootstrapper Python)" -ForegroundColor Cyan
$uvDir = Join-Path $publish "uv"
New-Item -ItemType Directory -Force -Path $uvDir | Out-Null
$uvZip = Join-Path $env:TEMP "uv-win.zip"
$uvUrl = "https://github.com/astral-sh/uv/releases/download/$UvVersion/uv-x86_64-pc-windows-msvc.zip"
Invoke-WebRequest $uvUrl -OutFile $uvZip

# Verification d'integrite : on calcule le SHA256 du zip telecharge.
$uvHash = (Get-FileHash $uvZip -Algorithm SHA256).Hash
Write-Host "    uv.zip SHA256 = $uvHash" -ForegroundColor DarkGray
if ($UvSha256) {
    if ($uvHash -ne $UvSha256.ToUpper()) {
        throw "Integrite uv.zip KO : attendu $UvSha256, obtenu $uvHash."
    }
    Write-Host "    integrite uv verifiee." -ForegroundColor DarkGray
} else {
    Write-Warning "SHA256 de reference (`$UvSha256) non renseigne : integrite NON verifiee. Colle le hash ci-dessus dans `$UvSha256."
}

Expand-Archive $uvZip -DestinationPath $uvDir -Force
if (-not (Test-Path (Join-Path $uvDir "uv.exe"))) { throw "uv.exe introuvable apres extraction." }

Write-Host "==> Packaging Velopack" -ForegroundColor Cyan
# Velopack cree par defaut un raccourci Menu Demarrer (pas de raccourci Bureau).
$icon = Join-Path $root "src\LocalTranscriber.Gui\Assets\app.ico"
$packArgs = @(
    "pack",
    "--packId", "LocalTranscriber",
    "--packTitle", "LocalTranscriber",
    "--packVersion", $Version,
    "--packDir", $publish,
    "--mainExe", "LocalTranscriber.exe",
    "--icon", $icon,
    "--outputDir", $releases
)
if ($ReleaseNotes -and (Test-Path $ReleaseNotes)) {
    $packArgs += @("--releaseNotes", (Resolve-Path $ReleaseNotes).Path)
}

# Signature de code (si un PFX est fourni) : Velopack signe les binaires .NET et le
# Setup.exe via signtool. Timestamp inclus pour que la signature survive a l'expiration.
if ($PfxBase64) {
    Write-Host "==> Signature de code activee" -ForegroundColor Cyan
    $pfxPath = Join-Path $env:TEMP "lt-codesign.pfx"
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($PfxBase64))
    $signParams = "/fd sha256 /f `"$pfxPath`" /p `"$PfxPassword`" /tr http://timestamp.digicert.com /td sha256"
    $packArgs += @("--signParams", $signParams)
}

vpk @packArgs

if ($PfxBase64) { Remove-Item (Join-Path $env:TEMP "lt-codesign.pfx") -Force -ErrorAction SilentlyContinue }

Write-Host "OK -> installeur (leger) dans $releases" -ForegroundColor Green
