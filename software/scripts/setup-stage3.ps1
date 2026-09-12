param([string]$Python="python", [string]$Wheelhouse="")
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
try {
    & $Python -c "import sys,platform; assert sys.version_info[:2]==(3,12) and platform.system()=='Windows' and platform.machine().lower() in ('amd64','x86_64'), 'Locked bundle requires Windows x64 Python 3.12'"
    if($LASTEXITCODE -ne 0){throw 'Unsupported Python for locked bundle'}
    & $Python -m venv .venv
    if($LASTEXITCODE -ne 0){throw 'Environment creation failed'}
    $taskPipArgs=@()
    if($Wheelhouse){$taskPipArgs=@('--no-index','--find-links',$Wheelhouse)}
    & .\.venv\Scripts\python.exe -m pip install @taskPipArgs --require-hashes -r python/requirements-stage3-win-py312.lock
    if($LASTEXITCODE -ne 0){throw 'Locked dependency install failed'}
    & .\.venv\Scripts\python.exe -m pip install --no-deps --no-build-isolation -e .\python
    if($LASTEXITCODE -ne 0){throw 'Local package install failed'}
} finally {Pop-Location}
