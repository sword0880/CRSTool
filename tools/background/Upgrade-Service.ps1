#Requires -Version 7.0
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$PackageDirectory,[string]$TrustedPublisherThumbprint,[switch]$AllowDevelopmentPackage)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerUpgradeTransaction.psm1') -Force
$name='CRSBackgroundService'
$program=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CRS/Background'))
$data=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CRS/Background'))
# 正式入口保持固定身份/目录，测试不通过CLI参数覆盖这些安全边界。
Assert-WorkerServiceRegistration $name $program -RequireAutomatic
$service=New-WorkerServiceOperations $name $program
Invoke-WorkerUpgradeTransaction -Data $data -Program $program -Source $PackageDirectory -Service $service -Publisher $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
Write-Output 'Worker升级已确认；旧程序与一致性快照保留，候选打开数据后禁止旧库覆盖回退。'
