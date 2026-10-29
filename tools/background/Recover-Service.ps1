#Requires -Version 7.0
#Requires -RunAsAdministrator
param([ValidateSet('Status','RetryCandidate','RestorePrevious','ResumeConfirmed')][string]$Action='Status',[string]$TrustedPublisherThumbprint,[switch]$AllowDevelopmentPackage,
    [ValidateRange(300,1800)][int]$StartupTimeoutSeconds=300)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerMaintenance.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'WorkerUpgradeTransaction.psm1') -Force
$name='CRSBackgroundService'
$program=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CRS/Background'))
$data=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CRS/Background'))
if($Action -eq 'Status'){
    $record=Read-WorkerUpgrade $data
    if(-not $record){throw '没有Worker升级日志，不推断恢复起点。'}
    $record | Select-Object Id,Stage,PreviousVersion,CandidateVersion,NewFactsPossible,UpdatedUtc
    return
}
Assert-WorkerServiceRegistration $name $program
$service=New-WorkerServiceOperations $name $program
Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action $Action -Publisher $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage -StartupTimeoutSeconds $StartupTimeoutSeconds
Write-Output 'Worker恢复完成；现有数据、失败候选及原始升级备份保留。'
