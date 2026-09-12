param(
    [string]$SdkRoot=(Join-Path $env:USERPROFILE '.pico-sdk'),
    [string]$NetworkConfig
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
if(!$NetworkConfig){$NetworkConfig=Join-Path $taskRoot '.artifacts/stage5/private/network.json'}
$taskConfig=Get-Content -Raw -LiteralPath $NetworkConfig | ConvertFrom-Json
if(!$taskConfig.ssid -or !$taskConfig.password){throw 'SSID and password are required'}
$taskAddress=$null
if(![Net.IPAddress]::TryParse([string]$taskConfig.host,[ref]$taskAddress) -or $taskAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork){throw 'Receiver must be an IPv4 address'}
if([int]$taskConfig.port -lt 1 -or [int]$taskConfig.port -gt 65535){throw 'Invalid TCP port'}
if($taskConfig.mode -and $taskConfig.mode -ne 'counter'){throw 'Only counter mode is implemented'}
if($taskConfig.country -and $taskConfig.country -ne 'US'){throw 'This bench firmware currently uses the US regulatory domain'}
$taskPrivate=Join-Path $taskRoot '.artifacts/stage5/private'
New-Item -ItemType Directory -Force $taskPrivate | Out-Null
# Credentials appear only in an ignored header and local build artifacts.
function CLiteral([string]$Value) {
    $taskBytes=[Text.Encoding]::UTF8.GetBytes($Value)
    return '"' + (($taskBytes | ForEach-Object { '\{0:000}' -f [Convert]::ToString($_,8).PadLeft(3,'0') }) -join '') + '"'
}
$taskHeader=@(
    '#pragma once',
    ('#define MND_SSID '+(CLiteral $taskConfig.ssid)),
    ('#define MND_PASSWORD '+(CLiteral $taskConfig.password)),
    ('#define MND_HOST '+(CLiteral $taskConfig.host)),
    ('#define MND_PORT '+[int]$taskConfig.port),
    ('#define MND_SEED '+[uint32]$taskConfig.seed+'u')
)
[IO.File]::WriteAllLines((Join-Path $taskPrivate 'private_config.h'),$taskHeader)
$taskBuild=Join-Path $taskRoot '.artifacts/stage5/build-stream'
$taskCmake=Join-Path $SdkRoot 'cmake/v3.31.5/bin/cmake.exe'
& $taskCmake -S (Join-Path $taskRoot 'firmware/pico_synthetic') -B $taskBuild -G Ninja "-DCMAKE_MAKE_PROGRAM=$SdkRoot/ninja/v1.12.1/ninja.exe" "-DPICO_SDK_PATH=$SdkRoot/sdk/2.2.0" "-DPICO_TOOLCHAIN_PATH=$SdkRoot/toolchain/14_2_Rel1" "-Dpicotool_DIR=$SdkRoot/picotool/2.2.0-a4/picotool" "-Dpioasm_DIR=$SdkRoot/tools/2.2.0/pioasm" "-DMND_PRIVATE_DIR=$taskPrivate" -DPICO_BOARD=pico2_w -DCMAKE_BUILD_TYPE=Release
if($LASTEXITCODE -ne 0){throw 'Pico configure failed'}
& $taskCmake --build $taskBuild --target multinodedaq_stream --parallel 4
if($LASTEXITCODE -ne 0){throw 'Pico build failed'}
Get-FileHash -LiteralPath (Join-Path $taskBuild 'multinodedaq_stream.uf2') -Algorithm SHA256
