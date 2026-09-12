$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskTools = Join-Path $taskRoot '.tools'
$taskSdk = Join-Path $taskTools 'dotnet'
$taskZip = Join-Path $taskTools 'dotnet-sdk-10.0.401.zip'
New-Item -ItemType Directory -Force -Path $taskSdk | Out-Null
if (!(Test-Path -LiteralPath $taskZip)) {
    Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-x64.zip' -OutFile $taskZip
}
$taskExpected = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
if ((Get-FileHash -LiteralPath $taskZip -Algorithm SHA512).Hash.ToLowerInvariant() -ne $taskExpected) { throw 'SDK checksum mismatch' }
Expand-Archive -LiteralPath $taskZip -DestinationPath $taskSdk -Force
& (Join-Path $taskSdk 'dotnet.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'SDK failed to start' }
