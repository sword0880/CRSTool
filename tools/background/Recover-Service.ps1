#Requires -Version 7.0
#Requires -RunAsAdministrator
param([ValidateSet('Status','RetryCandidate','RestorePrevious')][string]$Action='Status',
    [string]$TrustedPublisherThumbprint, [switch]$AllowDevelopmentPackage)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerMaintenance.psm1') -Force
$name='CRSBackgroundService'
$program=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'CRS/Background'))
$data=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'CRS/Background'))
$record=Read-WorkerUpgrade $data
if(-not $record){throw '没有Worker升级日志；不推断或创建恢复起点。'}
if($Action -eq 'Status'){
    # 只输出事务状态与发行指纹，不输出密钥、原件、连接串或服务数据路径。
    $record | Select-Object Id,Stage,PreviousVersion,CandidateVersion,NewFactsPossible,UpdatedUtc
    return
}
$registered=Get-CimInstance Win32_Service -Filter "Name='$name'"
if(-not $registered -or $registered.PathName -ne ('"'+(Join-Path $program 'CRS.Worker.Service.exe')+'"') -or
    $registered.StartName -ne 'NT AUTHORITY\LocalService'){throw '服务注册路径或身份不匹配。'}
if($record.Stage -in @('Committed','RolledBack')){throw '事务已经结束，不重新回退已确认版本。'}
$operation=[IO.FileStream]::new((Get-WorkerChild $data 'maintenance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
$record=Read-WorkerUpgrade $data
if($record.Stage -in @('Committed','RolledBack')){throw '事务已经结束。'}
Set-Service -Name $name -StartupType Disabled
Stop-Service -Name $name
(Get-Service -Name $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))
$lease=[IO.FileStream]::new((Get-WorkerChild $data 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $record=Read-WorkerUpgrade $data
    $paths=Get-WorkerUpgradePaths $program $record
    if($Action -eq 'RestorePrevious'){
        if($record.NewFactsPossible){throw '候选可能已写入新事实，拒绝恢复旧程序或旧库。'}
        $old=if(Test-Path -LiteralPath $paths.Previous){$paths.Previous}else{$paths.Active}
        $null=Test-WorkerPackage $old $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
        Restore-WorkerPrevious $data $program $record
    }else{
        # 重试只继续候选，保留所有现有数据库及风险标记，不导入旧快照。
        Test-WorkerSnapshot $data $record
        if(-not (Test-Path -LiteralPath $paths.Active) -and (Test-Path -LiteralPath $paths.Stage)){
            $null=Test-WorkerPackage $paths.Stage $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
            if((Get-FileHash -LiteralPath (Join-Path $paths.Stage 'release.json')).Hash -ne $record.CandidateReleaseHash){throw '暂存程序不是候选发行。'}
            [IO.Directory]::Move($paths.Stage,$paths.Active)
        }
        $null=Test-WorkerPackage $paths.Active $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
        if((Get-FileHash -LiteralPath (Join-Path $paths.Active 'release.json')).Hash -ne $record.CandidateReleaseHash){throw '活动程序不是候选发行；可在无新事实时恢复旧程序。'}
        Set-WorkerUpgradeStage $data $record 'Switched'
    }
}finally{$lease.Dispose()}
& sc.exe config $name start= delayed-auto | Out-Null
if($LASTEXITCODE -ne 0){throw '服务恢复启动策略失败；维护日志保留。'}
try{
    Start-Service -Name $name
    $deadline=[DateTime]::UtcNow.AddSeconds(40);$ready=$false
    do{
        & (Join-Path $program 'CRS.Worker.Service.exe') --health-probe | Out-Null
        $ready=$LASTEXITCODE -eq 0
        if(-not $ready){Start-Sleep -Milliseconds 250}
    }while(-not $ready -and [DateTime]::UtcNow -lt $deadline)
    $current=Read-WorkerUpgrade $data
    if(-not $ready -or $current.Stage -notin @('Healthy','RolledBack')){throw '恢复后应用未就绪。'}
    if($current.Stage -eq 'Healthy'){Set-WorkerUpgradeStage $data $current 'Committed'}
    Write-Output 'Worker恢复完成；既有数据和原始升级备份保留。'
}catch{
    Set-Service -Name $name -StartupType Disabled
    Stop-Service -Name $name
    (Get-Service -Name $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))
    $current=Read-WorkerUpgrade $data
    if($current.Stage -notin @('Committed','RolledBack')){Set-WorkerUpgradeStage $data $current 'NeedsManualRepair'}
    throw
}
}finally{$operation.Dispose()}
