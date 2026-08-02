<#
Installe l'environnement de dev pinné (stack whisperx 3.1 / pyannote 3.1) dans le
venv engine\.venv existant. torch CPU 2.2.2. Log UTF-8 + detection d'echec.
Marqueur final : DEV-INSTALL-DONE fail=X
#>
param([switch]$Cuda)

$root = Split-Path -Parent $PSScriptRoot
$engine = Join-Path $root "engine"
$py = Join-Path $engine ".venv\Scripts\python.exe"
$log = Join-Path $root "build\install-dev.log"
$fail = 0
function Log($m) { $m | Out-File -FilePath $log -Encoding utf8 -Append }
"=== install-dev $(Get-Date -Format o) ===" | Out-File -FilePath $log -Encoding utf8

Log "pip upgrade..."
& $py -m pip install --upgrade pip *>&1 | Out-File -FilePath $log -Encoding utf8 -Append

$torchIndex = if ($Cuda) { "https://download.pytorch.org/whl/cu121" } else { "https://download.pytorch.org/whl/cpu" }
Log "torch 2.2.2 ($torchIndex)..."
& $py -m pip install torch==2.2.2 torchaudio==2.2.2 --index-url $torchIndex *>&1 | Out-File -FilePath $log -Encoding utf8 -Append
if ($LASTEXITCODE -ne 0) { $fail = 1; Log "ECHEC torch (rc=$LASTEXITCODE)" }

Log "requirements..."
& $py -m pip install -r (Join-Path $engine "requirements.txt") *>&1 | Out-File -FilePath $log -Encoding utf8 -Append
if ($LASTEXITCODE -ne 0) { $fail = 1; Log "ECHEC requirements (rc=$LASTEXITCODE)" }

Log "point d'entree console (editable)..."
& $py -m pip install -e $engine --no-deps *>&1 | Out-File -FilePath $log -Encoding utf8 -Append
if ($LASTEXITCODE -ne 0) { $fail = 1; Log "ECHEC editable (rc=$LASTEXITCODE)" }

Log "selftest..."
& (Join-Path $engine ".venv\Scripts\transcriber-engine.exe") --selftest *>&1 | Out-File -FilePath $log -Encoding utf8 -Append

Log "DEV-INSTALL-DONE fail=$fail"
