param([int]$Seconds=7200,[int]$Nodes=16)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskDotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet='dotnet' }
Push-Location $taskRoot
try {
    & $taskDotnet run --project tests/Elf.Recording.Tests -c Release --no-build -- --benchmark --nodes $Nodes --seconds $Seconds
    if ($LASTEXITCODE -ne 0) { throw 'Stage 2 recording qualification failed' }
} finally { Pop-Location }
