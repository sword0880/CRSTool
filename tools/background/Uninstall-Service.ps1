#Requires -Version 7.0
#Requires -RunAsAdministrator
param()
$ErrorActionPreference='Stop'
$name='CRSBackgroundService'
$service=Get-Service -Name $name -ErrorAction SilentlyContinue
if(-not $service){throw '未找到 CRS 后台服务。'}
$target=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CRS/Background'))
$expected='"'+(Join-Path $target 'CRS.Worker.Service.exe')+'"'
$data=Join-Path $env:ProgramData 'CRS/Background'
Import-Module (Join-Path $PSScriptRoot 'WorkerMaintenance.psm1') -Force
$operation=[IO.FileStream]::new((Get-WorkerChild $data 'maintenance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
$maintenance=Read-WorkerUpgrade $data
if($maintenance -and $maintenance.Stage -notin @('Committed','RolledBack')){throw '存在未解决升级，请先完成恢复；卸载不能绕过维护保护。'}
$registered=Get-CimInstance Win32_Service -Filter "Name='$name'"
if($registered.PathName -ne $expected -or $registered.StartName -ne 'NT AUTHORITY\LocalService'){throw '服务路径或身份与本安装器不一致，拒绝删除。'}
Stop-Service -Name $name
$service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))
& sc.exe delete $name
if($LASTEXITCODE -ne 0){throw 'SCM 卸载失败。'}
# 仅删除已核验的固定程序目录，默认永久保留 ProgramData 数据及密钥。
if(-not $target.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '卸载路径越界。'}
if((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '程序目录为链接，拒绝递归删除。'}
foreach($item in Get-ChildItem -LiteralPath $target -Recurse -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '程序目录包含链接，拒绝递归删除。'}}
Remove-Item -LiteralPath $target -Recurse -Force
Write-Output '服务和程序已卸载；ProgramData/CRS/Background 数据、执行历史和服务密钥已保留。'
}finally{$operation.Dispose()}
