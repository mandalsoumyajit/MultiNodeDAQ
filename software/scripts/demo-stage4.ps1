param([int]$Seconds=300)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskPackaged=Test-Path -LiteralPath (Join-Path $PSScriptRoot 'MultiNodeDAQ.Desktop.exe')
if($taskPackaged){
    $taskDesktop=Join-Path $PSScriptRoot 'MultiNodeDAQ.Desktop.exe'
    $taskHost=Join-Path $PSScriptRoot 'host/MultiNodeDAQ.Host.exe'
    $taskSimulator=Join-Path $PSScriptRoot 'simulator/MultiNodeDAQ.Simulator.exe'
}else{
    $taskDesktop=Join-Path $taskRoot 'src/MultiNodeDAQ.Desktop/bin/Release/net10.0-windows/MultiNodeDAQ.Desktop.exe'
    $taskHost=Join-Path $taskRoot 'src/MultiNodeDAQ.Host/bin/Release/net10.0/MultiNodeDAQ.Host.exe'
    $taskSimulator=Join-Path $taskRoot 'src/MultiNodeDAQ.Simulator/bin/Release/net10.0/MultiNodeDAQ.Simulator.exe'
    $env:DOTNET_ROOT=Join-Path $taskRoot '.tools/dotnet'
}
$taskData=Join-Path $env:LOCALAPPDATA ('MultiNodeDAQ/demos/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskData -Force | Out-Null
$taskScenario=Join-Path $taskData 'scenario.json'
@{Nodes=2;Seconds=$Seconds;Mode='tone';Faults=@(@{Node=1;AtSeconds=15;Kind='disconnect';DurationSeconds=2})} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskScenario
$env:MULTINODEDAQ_IPC_TOKEN=[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:MULTINODEDAQ_IPC_PORT='45101'
$taskService=Start-Process -FilePath $taskHost -ArgumentList @('--ipc-port','45101','--seconds',($Seconds+15),'--record',('"'+(Join-Path $taskData 'recording')+'"')) -WorkingDirectory $taskData -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 1
if($taskService.HasExited){throw 'Demo service could not start; check whether port 45100 or 45101 is already in use.'}
Start-Process -FilePath $taskSimulator -ArgumentList @('--scenario',('"'+$taskScenario+'"')) -WorkingDirectory $taskData -WindowStyle Hidden | Out-Null
Start-Process -FilePath $taskDesktop -WorkingDirectory $taskData | Out-Null
Write-Output "Demo data: $taskData. The recorder runs independently for up to $($Seconds+15) seconds."
