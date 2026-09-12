param([int]$Seconds = 30)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskDotnet = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
Push-Location $taskRoot
try {
    Write-Host "Running two simulated units for $Seconds seconds over loopback TCP. No recording."
    & $taskDotnet run --project tests/MultiNodeDAQ.Integration.Tests -c Release --no-build -- --benchmark --nodes 2 --seconds $Seconds
    if ($LASTEXITCODE -ne 0) { throw 'Demo failed' }
    Write-Host 'Health telemetry and summaries: .artifacts/stage1/'
} finally { Pop-Location }
