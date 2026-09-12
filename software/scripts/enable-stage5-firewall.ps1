# Run once in an Administrator PowerShell for the local Stage 5 bench test.
# Restricts access to this receiver program, Wi-Fi interface, LAN and TCP port.
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskProgram=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if(!(Test-Path -LiteralPath $taskProgram)){throw 'Local .NET runtime not found'}
if(!(Get-NetIPAddress -InterfaceAlias 'Wi-Fi' -AddressFamily IPv4 | Where-Object IPAddress -eq '192.168.1.180')){throw 'Laptop IP changed; update the bench configuration first'}
if(!(Get-NetFirewallRule -Name 'MultiNodeDAQ-Stage5-WiFi' -ErrorAction SilentlyContinue)){
    New-NetFirewallRule -Name 'MultiNodeDAQ-Stage5-WiFi' -DisplayName 'MultiNodeDAQ Stage 5 Wi-Fi receiver' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45230 -LocalAddress 192.168.1.180 -RemoteAddress 192.168.1.0/24 -InterfaceAlias 'Wi-Fi' -Profile Any -Program $taskProgram | Out-Null
}
