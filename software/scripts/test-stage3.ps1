$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    & .\scripts\test.ps1 -Python "$taskRoot\.venv\Scripts\python.exe"
    if($LASTEXITCODE -ne 0){throw 'Regressions failed'}
    & .\.venv\Scripts\python.exe scripts/test-stage3-live.py
    if($LASTEXITCODE -ne 0){throw 'Live worker qualification failed'}
} finally {Pop-Location}
