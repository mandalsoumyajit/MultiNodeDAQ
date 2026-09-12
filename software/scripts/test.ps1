param([string]$Python = "python", [string]$Dotnet = "")
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (-not $Dotnet) {
    $taskLocalSdk = Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $taskLocalSdk) { $Dotnet = $taskLocalSdk } else { $Dotnet = 'dotnet' }
}
Push-Location $taskRoot
try {
    & $Dotnet restore ElfDaq.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
    & $Dotnet build ElfDaq.slnx --no-restore -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    & $Dotnet run --project tests/Elf.Contracts.Tests -c Release --no-build -- fixtures
    if ($LASTEXITCODE -ne 0) { throw 'C# tests failed' }
    & $Dotnet run --project tests/Elf.Integration.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Stage 1 integration tests failed' }
    & $Python -m unittest discover -s python/tests -v
    if ($LASTEXITCODE -ne 0) { throw 'Python tests failed' }
} finally { Pop-Location }
