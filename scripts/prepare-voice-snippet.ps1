<#
.SYNOPSIS
    Prépare un snippet de voix propre (mono 16 kHz) pour l'identification des locuteurs.
.DESCRIPTION
    Extrait un segment d'un enregistrement existant et le normalise en WAV mono 16 kHz,
    nommé d'après le locuteur, dans le dossier voices/ cible. Nécessite FFmpeg dans le PATH.
.PARAMETER Name
    Nom du locuteur = nom du fichier de sortie (ex. "Jonathan" -> Jonathan.wav).
.PARAMETER Input
    Fichier audio source (n'importe quel format lisible par FFmpeg).
.PARAMETER VoicesDir
    Dossier voices/ de destination (défaut : .\voices).
.PARAMETER Start
    Début de l'extrait, en secondes (défaut : 0).
.PARAMETER Duration
    Durée de l'extrait, en secondes (défaut : 20).
.EXAMPLE
    .\prepare-voice-snippet.ps1 -Name "Marie" -Input "reunion.mp3" -VoicesDir ".\voices" -Start 12 -Duration 20
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Input,
    [string]$VoicesDir = ".\voices",
    [double]$Start = 0,
    [int]$Duration = 20
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    throw "FFmpeg est introuvable dans le PATH. Installez-le (ex. winget install Gyan.FFmpeg)."
}
if (-not (Test-Path $Input)) {
    throw "Fichier source introuvable : $Input"
}

# Nom de fichier sûr (sans caractères interdits).
$safe = ($Name -replace '[\\/:*?"<>|]', '_').Trim()
if ([string]::IsNullOrWhiteSpace($safe)) { throw "Nom de locuteur invalide." }

New-Item -ItemType Directory -Force -Path $VoicesDir | Out-Null
$out = Join-Path $VoicesDir "$safe.wav"

Write-Host "Extraction de $Duration s (depuis $Start s) de '$Input' -> '$out'" -ForegroundColor Cyan

# -ss avant -i = seek rapide ; mono 16 kHz PCM 16 bits = format attendu par le moteur.
& ffmpeg -y -ss $Start -t $Duration -i "$Input" -ac 1 -ar 16000 -sample_fmt s16 "$out"

if ($LASTEXITCODE -ne 0) { throw "FFmpeg a échoué (code $LASTEXITCODE)." }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "OK : $out ($size Ko). Placez ce dossier voices/ dans votre projet et activez l'identification dans la GUI." -ForegroundColor Green
