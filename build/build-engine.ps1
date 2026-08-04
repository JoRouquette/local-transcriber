<#
.SYNOPSIS
    Gele le moteur Python en un executable autonome (PyInstaller).
.DESCRIPTION
    Cree un venv, installe PyTorch (CPU ou CUDA selon -Cuda), les dependances, puis
    lance PyInstaller avec transcriber-engine.spec. Resultat : engine\dist\transcriber-engine\.
.EXAMPLE
    .\build-engine.ps1            # build CPU
    .\build-engine.ps1 -Cuda      # build GPU (CUDA 12.1)
#>
param(
    [switch]$Cuda
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$engineDir = Join-Path $root "engine"
Set-Location $engineDir

Write-Host "==> Creation du venv" -ForegroundColor Cyan
if (-not (Test-Path ".venv")) { python -m venv .venv }
$py = Join-Path $engineDir ".venv\Scripts\python.exe"

& $py -m pip install --upgrade pip

Write-Host "==> Installation de PyTorch (pins de constraints-torch-cpu.txt, aligne sur la stack)" -ForegroundColor Cyan
# Versions epinglees dans engine\constraints-torch-cpu.txt (torch/torchaudio 2.2.2).
# Seul l'index change entre CPU et CUDA ; les pins restent centralises dans le fichier.
$torchConstraints = Join-Path $engineDir "constraints-torch-cpu.txt"
$torchIndex = if ($Cuda) { "https://download.pytorch.org/whl/cu121" } else { "https://download.pytorch.org/whl/cpu" }
& $py -m pip install -r $torchConstraints --index-url $torchIndex

Write-Host "==> Installation des dependances du moteur" -ForegroundColor Cyan
& $py -m pip install -r requirements.txt

Write-Host "==> Test d'environnement (selftest)" -ForegroundColor Cyan
& $py -m transcriber_engine --selftest

Write-Host "==> PyInstaller (gel du moteur)" -ForegroundColor Cyan
& $py -m PyInstaller transcriber-engine.spec --noconfirm --clean

Write-Host "OK -> $engineDir\dist\transcriber-engine\transcriber-engine.exe" -ForegroundColor Green
