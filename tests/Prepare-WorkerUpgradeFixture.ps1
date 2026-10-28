#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Source=(Join-Path $PSScriptRoot 'CRS.Tests.Background/bin/Release/net10.0-windows')
)
$ErrorActionPreference='Stop'
$Source=[IO.Path]::GetFullPath($Source)
$Destination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $Destination){throw 'Worker fixture destination must be new.'}
foreach($name in @('CRS.Worker.Service.exe','CRS.Tests.Background.dll','CRS.Worker.FixturePlugin.dll','CRS.Common.dll','Runner/CRS.Worker.PluginRunner.exe')){
    if(-not (Test-Path -LiteralPath (Join-Path $Source $name) -PathType Leaf)){throw "Worker fixture source is missing $name."}
}
# 测试输出副本仅含合成发行元数据；不可安装或作为生产包使用。
New-Item -ItemType Directory -Path $Destination | Out-Null
Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force}
[IO.File]::WriteAllText((Join-Path $Destination 'release.json'),
    '{"format":1,"product":"CRS.Worker.Service","version":"2.0.0","files":{}}',[Text.UTF8Encoding]::new($false))
$pluginDirectory=Join-Path $Destination 'Plugins/crs.fixture/1.0.0'
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $Source 'CRS.Worker.FixturePlugin.dll') -Destination $pluginDirectory
Copy-Item -LiteralPath (Join-Path $Source 'CRS.Common.dll') -Destination $pluginDirectory
$manifest=@{id='crs.fixture';version='1.0.0';hostContract='2.0';backgroundEntry=@{assembly='CRS.Worker.FixturePlugin.dll';type='CRS.Worker.FixturePlugin.GoodEntry'}}
[IO.File]::WriteAllText((Join-Path $pluginDirectory 'plugin.json'),(ConvertTo-Json -InputObject $manifest -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
$files=@{}
Get-ChildItem -LiteralPath $pluginDirectory -File | ForEach-Object {$files[$_.Name]=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
$trusted=@(@{id='crs.fixture';version='1.0.0';directory='crs.fixture/1.0.0';files=$files})
[IO.File]::WriteAllText((Join-Path $Destination 'trusted-background-plugins.json'),(ConvertTo-Json -InputObject $trusted -Depth 8 -Compress),[Text.UTF8Encoding]::new($false))
Write-Output "Synthetic Worker fixture prepared: $Destination. Not a distributable package."
