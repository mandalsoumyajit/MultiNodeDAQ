param([string]$OutputDirectory=(Join-Path $PSScriptRoot '../.artifacts/stage5/pc-power-diagnostics'))
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$principal=[Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {throw 'Run this diagnostic collector from an Administrator PowerShell window.'}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
& powercfg /sleepstudy /duration 3 /output (Join-Path $OutputDirectory 'sleepstudy.html')
& powercfg /systempowerreport /output (Join-Path $OutputDirectory 'system-power.html')
& powercfg /requests | Out-File (Join-Path $OutputDirectory 'power-requests.txt')
Get-ChildItem -LiteralPath "$env:SystemRoot/LiveKernelReports" -Filter '*.dmp' -Recurse -ErrorAction Continue | Select-Object FullName,Length,LastWriteTime | Export-Csv (Join-Path $OutputDirectory 'dump-inventory.csv') -NoTypeInformation
Write-Output "Diagnostics saved to $OutputDirectory. No settings changed; no files uploaded."
