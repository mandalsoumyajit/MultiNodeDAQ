param([string]$InstallRoot=(Join-Path $env:LOCALAPPDATA 'Programs/MultiNodeDAQ'), [switch]$NoShortcut)
$ErrorActionPreference='Stop'
$taskManifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Raw | ConvertFrom-Json
$taskSource=[IO.Path]::GetFullPath($PSScriptRoot)+[IO.Path]::DirectorySeparatorChar
foreach($taskFile in $taskManifest.files){
    if([IO.Path]::IsPathRooted($taskFile.path)){throw 'Invalid absolute package path'}
    $taskPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $taskFile.path))
    if(-not $taskPath.StartsWith($taskSource,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid package path'}
    if((Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash -ne $taskFile.sha256){throw "Package hash mismatch: $($taskFile.path)"}
}
if($taskManifest.version -notmatch '^[a-zA-Z0-9.-]+$' -or $taskManifest.commit -notmatch '^[a-f0-9]{40}$'){throw 'Invalid release identity'}
$taskVersion=$taskManifest.version+'-'+$taskManifest.commit.Substring(0,7)+$(if($taskManifest.dirty){'-dirty'})
$taskDestination=Join-Path $InstallRoot $taskVersion
if(Test-Path -LiteralPath $taskDestination){throw "Version already installed: $taskDestination"}
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
foreach($taskFile in $taskManifest.files){
    $taskTarget=Join-Path $taskDestination $taskFile.path
    New-Item -ItemType Directory -Path (Split-Path $taskTarget -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile.path) -Destination $taskTarget
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release.json') -Destination $taskDestination
if(-not $NoShortcut){
    $taskShell=New-Object -ComObject WScript.Shell
    $taskShortcut=$taskShell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'MultiNodeDAQ.lnk'))
    $taskShortcut.TargetPath=Join-Path $taskDestination 'MultiNodeDAQ.Desktop.exe'
    $taskShortcut.WorkingDirectory=$taskDestination
    $taskShortcut.Save()
}
Write-Output "Installed $taskVersion. Previous versions remain available for rollback. Close or finalize the old recorder before switching service versions."
