param([string]$SdkRoot=(Join-Path $env:USERPROFILE '.pico-sdk'))
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskBuild=Join-Path $taskRoot '.artifacts/stage5/build-diagnostic'
$taskCmake=Join-Path $SdkRoot 'cmake/v3.31.5/bin/cmake.exe'
$taskNinja=Join-Path $SdkRoot 'ninja/v1.12.1/ninja.exe'
$taskCompiler=Join-Path $SdkRoot 'toolchain/14_2_Rel1'
$taskSdk=Join-Path $SdkRoot 'sdk/2.2.0'
$taskPicotool=Join-Path $SdkRoot 'picotool/2.2.0-a4/picotool'
& $taskCmake -S (Join-Path $taskRoot 'firmware/pico_synthetic') -B $taskBuild -G Ninja "-DCMAKE_MAKE_PROGRAM=$taskNinja" "-DPICO_SDK_PATH=$taskSdk" "-DPICO_TOOLCHAIN_PATH=$taskCompiler" "-Dpicotool_DIR=$taskPicotool" "-Dpioasm_DIR=$SdkRoot/tools/2.2.0/pioasm" -DPICO_BOARD=pico2_w -DCMAKE_BUILD_TYPE=Release
if($LASTEXITCODE -ne 0){throw 'Pico configure failed'}
& $taskCmake --build $taskBuild --parallel 4
if($LASTEXITCODE -ne 0){throw 'Pico build failed'}
Get-FileHash -LiteralPath (Join-Path $taskBuild 'multinodedaq_diagnostic.uf2') -Algorithm SHA256
