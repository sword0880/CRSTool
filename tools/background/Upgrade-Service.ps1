#Requires -Version 7.0
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$PackageDirectory, [string]$TrustedPublisherThumbprint, [switch]$AllowDevelopmentPackage)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerMaintenance.psm1') -Force
$name = 'CRSBackgroundService'
$program = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CRS/Background'))
$data = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CRS/Background'))
Assert-WorkerNoLinks $program; Assert-WorkerNoLinks $data
$registered = Get-CimInstance Win32_Service -Filter "Name='$name'"
if (-not $registered -or $registered.PathName -ne ('"'+(Join-Path $program 'CRS.Worker.Service.exe')+'"') -or
    $registered.StartName -ne 'NT AUTHORITY\LocalService' -or $registered.StartMode -ne 'Auto') { throw '维护仅支持固定路径、LocalService和自动启动的正式服务。' }
$source = [IO.Path]::GetFullPath($PackageDirectory)
if ($source -eq $program -or $source.StartsWith($program+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw '候选必须在活动程序目录之外。' }
$candidate = Test-WorkerPackage $source $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
$previous = Test-WorkerPackage $program $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
if (-not $candidate.ContainsKey('maintenanceFormat') -or $candidate.maintenanceFormat -ne 1 -or
    -not $candidate.ContainsKey('upgradeFromVersions') -or $previous.version -notin $candidate.upgradeFromVersions -or
    ([Version]$candidate.version -le [Version]$previous.version -and -not $AllowDevelopmentPackage)) { throw '候选没有兼容的Worker维护协议/版本声明，或不是递增版本。' }
$existing = Read-WorkerUpgrade $data
if ($existing -and $existing.Stage -notin @('Committed','RolledBack')) { throw '存在未解决维护日志，请使用Recover-Service.ps1，禁止覆盖。' }
# 首先禁用自动启动，避免停机后断电使尚无保护的旧二进制自动打开数据。
$operation = [IO.FileStream]::new((Get-WorkerChild $data 'maintenance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$lease = $null; $record = $null; $prepared = $false
try {
    # 全流程独占维护租约，防止两个管理员协调器互相停止服务或覆盖状态。
    $existing = Read-WorkerUpgrade $data
    if ($existing -and $existing.Stage -notin @('Committed','RolledBack')) { throw '存在未解决维护日志。' }
    New-Item -ItemType Directory -Path (Get-WorkerChild $data 'maintenance') -Force | Out-Null
    # 旧日志随下一次维护归档，保留跨次事务审计；绝不覆盖未解决事务。
    if ($existing) { Write-WorkerJson (Get-WorkerChild $data ('maintenance/'+$existing.Id+'.json')) $existing }
    $record = @{ Format=1; Id=[Guid]::NewGuid().ToString('N'); PreviousReleaseHash=(Get-FileHash -LiteralPath (Join-Path $program 'release.json')).Hash;
        CandidateReleaseHash=(Get-FileHash -LiteralPath (Join-Path $source 'release.json')).Hash; PreviousVersion=$previous.version; CandidateVersion=$candidate.version;
        Stage='Prepared'; NewFactsPossible=$false; UpdatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); BackupManifestHash=$null }
    Set-WorkerUpgradeStage $data $record 'Prepared'
    $prepared = $true
    # 先记录维护意图，再禁用SCM；即使此后断电也有可查询的恢复起点。
    Set-Service -Name $name -StartupType Disabled
    Stop-Service -Name $name
    (Get-Service -Name $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))
    $lease = [IO.FileStream]::new((Get-WorkerChild $data 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    New-WorkerSnapshot $data $record
    Set-WorkerUpgradeStage $data $record 'BackedUp'
    $paths = Get-WorkerUpgradePaths $program $record
    Copy-WorkerPackage $source $paths.Stage $program
    $null = Test-WorkerPackage $paths.Stage $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
    Set-WorkerUpgradeStage $data $record 'Staged'
    Switch-WorkerPackage $data $program $record
    $lease.Dispose(); $lease=$null
    & sc.exe config $name start= delayed-auto | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '候选启动策略配置失败。' }
    Start-Service -Name $name
    $deadline = [DateTime]::UtcNow.AddSeconds(40); $ready=$false
    do {
        & (Join-Path $program 'CRS.Worker.Service.exe') --health-probe | Out-Null
        $ready = $LASTEXITCODE -eq 0
        if (-not $ready) { Start-Sleep -Milliseconds 250 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    $record = Read-WorkerUpgrade $data
    if (-not $ready -or $record.Stage -ne 'Healthy') { throw '候选未通过应用健康检查。' }
    Set-WorkerUpgradeStage $data $record 'Committed'
    Write-Output 'Worker升级已确认；旧程序与一致性快照保留，候选启动后禁止旧库覆盖回退。'
}
catch {
    if (-not $prepared) { throw } // 准入失败不影响原服务运行。
    # 不自动恢复旧库；尤其不能用脚本启动前缓存的false覆盖宿主已落盘的新事实标记。
    Set-Service -Name $name -StartupType Disabled
    if ((Get-Service -Name $name).Status -ne 'Stopped') {
        Stop-Service -Name $name
        (Get-Service -Name $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))
    }
    if ($lease) { $lease.Dispose(); $lease=$null }
    $current = Read-WorkerUpgrade $data
    if ($current -and $current.Stage -notin @('Committed','RolledBack')) { Set-WorkerUpgradeStage $data $current 'NeedsManualRepair' }
    throw
}
finally { if ($lease) { $lease.Dispose() }; $operation.Dispose() }
