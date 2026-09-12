param([int]$Seconds = 1800, [string]$Python = "python", [string]$Dotnet = "")
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (-not $Dotnet) {
    $taskLocalSdk = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $taskLocalSdk) { $Dotnet = $taskLocalSdk } else { $Dotnet = 'dotnet' }
}
Push-Location $taskRoot
try {
    foreach ($taskNodes in @(2,16)) {
        & $Dotnet run --project tests/MultiNodeDAQ.Integration.Tests -c Release --no-build -- --benchmark --nodes $taskNodes --seconds $Seconds
        if ($LASTEXITCODE -ne 0) { throw "Benchmark failed: $taskNodes nodes" }
    }
    & $Python scripts/analyze_stage1.py --seconds $Seconds
    if ($LASTEXITCODE -ne 0) { throw 'Memory or continuity gate failed' }
} finally { Pop-Location }
