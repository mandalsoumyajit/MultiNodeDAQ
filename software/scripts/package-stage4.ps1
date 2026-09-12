param([string]$OutputDirectory="")
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskDotnet=Join-Path $taskRoot '.tools/dotnet/dotnet.exe'
if(-not (Test-Path -LiteralPath $taskDotnet)){$taskDotnet='dotnet'}
Push-Location $taskRoot
try {
    $taskCommit=(git rev-parse HEAD).Trim()
    $taskDirty=[bool](git status --porcelain)
    if(-not $OutputDirectory){$OutputDirectory=Join-Path $taskRoot ('.artifacts/stage4/package-'+$taskCommit.Substring(0,7)+$(if($taskDirty){'-dirty'}))}
    $OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
    if(Test-Path -LiteralPath $OutputDirectory){throw 'Package directory must be new'}
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    foreach($taskProject in @('Desktop','Host','Simulator')) {
        $taskDestination=if($taskProject -eq 'Desktop'){$OutputDirectory}else{Join-Path $OutputDirectory $taskProject.ToLowerInvariant()}
        & $taskDotnet publish "src/MultiNodeDAQ.$taskProject" -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json -p:PublishSingleFile=false -p:RestoreLockedMode=true -o $taskDestination
        if($LASTEXITCODE -ne 0){throw "Publishing $taskProject failed"}
    }
    Copy-Item -LiteralPath (Join-Path $taskRoot '../LICENSE') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $taskRoot 'docs/stage4-operation.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-stage4.ps1') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'demo-stage4.ps1') -Destination $OutputDirectory
    $taskFiles=Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | ForEach-Object { @{path=[IO.Path]::GetRelativePath($OutputDirectory,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} }
    @{version='0.4.0-stage4';commit=$taskCommit;dirty=$taskDirty;platform='win-x64';files=@($taskFiles)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release.json')
    Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath ($OutputDirectory+'.zip')
    Get-FileHash -LiteralPath ($OutputDirectory+'.zip') -Algorithm SHA256
} finally {Pop-Location}
