Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerMaintenance.psm1')

function Assert-WorkerServiceRegistration([string]$Name,[string]$Program,[switch]$RequireAutomatic){
    $registered=Get-CimInstance Win32_Service -Filter "Name='$Name'"
    if(-not $registered -or $registered.PathName -ne ('"'+(Join-Path $Program 'CRS.Worker.Service.exe')+'"') -or
        $registered.StartName -ne 'NT AUTHORITY\LocalService' -or ($RequireAutomatic -and $registered.StartMode -ne 'Auto')){
        throw '维护服务的路径、LocalService身份或启动策略不匹配。'
    }
}
function New-WorkerServiceOperations([string]$Name,[string]$Program,[scriptblock]$HealthProbe=$null){
    # 正式CLI在构造前固定并核对名称/路径；隔离验收复用同一SCM操作，仅使用测试自有服务。
    $disable={Set-Service -Name $Name -StartupType Disabled}.GetNewClosure()
    $stop={
        $service=Get-Service -Name $Name
        try{
            if($service.Status -ne 'Stopped'){Stop-Service -Name $Name}
            $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))
        }finally{$service.Dispose()}
    }.GetNewClosure()
    $enable={
        & sc.exe config $Name start= delayed-auto | Out-Null
        if($LASTEXITCODE -ne 0){throw '服务启动策略配置失败。'}
    }.GetNewClosure()
    $start={Start-Service -Name $Name}.GetNewClosure()
    if(-not $HealthProbe){
        $HealthProbe={
            & (Join-Path $Program 'CRS.Worker.Service.exe') --health-probe | Out-Null
            return $LASTEXITCODE -eq 0
        }.GetNewClosure()
    }
    return @{Disable=$disable;Stop=$stop;Enable=$enable;Start=$start;Health=$HealthProbe}
}
function Wait-WorkerHealthy([hashtable]$Service){
    # 候选在打开数据库前逐份核验一致性快照；数千份小文件可超过原40秒。
    # 保持有界等待，但不能在校验期间反复停止服务，或绕过其完整性检查。
    $deadline=[DateTime]::UtcNow.AddSeconds(180)
    do{
        if(& $Service.Health){return}
        Start-Sleep -Milliseconds 250
    }while([DateTime]::UtcNow -lt $deadline)
    throw '应用健康检查未就绪。'
}
function Stop-WorkerForRepair([string]$Data,[hashtable]$Service){
    & $Service.Disable
    & $Service.Stop
    # 必须等服务退出后再读标记；不能拿候选启动前缓存的false覆盖最新风险。
    $current=Read-WorkerUpgrade $Data
    if($current -and $current.Stage -notin @('Committed','RolledBack')){Set-WorkerUpgradeStage $Data $current 'NeedsManualRepair'}
}
function Invoke-WorkerUpgradeTransaction {
    param([string]$Data,[string]$Program,[string]$Source,[hashtable]$Service,
        [string]$Publisher,[switch]$Development,[scriptblock]$Checkpoint=$null)
    Assert-WorkerNoLinks $Data;Assert-WorkerNoLinks $Program
    $Program=[IO.Path]::GetFullPath($Program);$Source=[IO.Path]::GetFullPath($Source)
    if($Source -eq $Program -or $Source.StartsWith($Program+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '候选必须位于活动程序目录之外。'}
    $operation=[IO.FileStream]::new((Get-WorkerChild $Data 'maintenance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $lease=$null;$prepared=$false
    try{
        $existing=Read-WorkerUpgrade $Data
        if($existing -and $existing.Stage -notin @('Committed','RolledBack')){throw '存在未解决维护日志。'}
        $candidate=Test-WorkerPackage $Source $Publisher -Development:$Development
        $previous=Test-WorkerPackage $Program $Publisher -Development:$Development
        if(-not $candidate.ContainsKey('maintenanceFormat') -or $candidate.maintenanceFormat -ne 1 -or
            -not $candidate.ContainsKey('upgradeFromVersions') -or $previous.version -notin $candidate.upgradeFromVersions -or
            ([Version]$candidate.version -le [Version]$previous.version -and -not $Development)){throw '候选维护协议/来源版本不兼容，或生产版本没有递增。'}
        New-Item -ItemType Directory -Path (Get-WorkerChild $Data 'maintenance') -Force | Out-Null
        if($existing){Write-WorkerJson (Get-WorkerChild $Data ('maintenance/'+$existing.Id+'.json')) $existing}
        $record=@{Format=1;Id=[Guid]::NewGuid().ToString('N');PreviousReleaseHash=(Get-FileHash (Join-Path $Program 'release.json')).Hash;
            CandidateReleaseHash=(Get-FileHash (Join-Path $Source 'release.json')).Hash;PreviousVersion=$previous.version;CandidateVersion=$candidate.version;
            Stage='Prepared';NewFactsPossible=$false;UpdatedUtc=[DateTimeOffset]::UtcNow.ToString('O');BackupManifestHash=$null}
        Set-WorkerUpgradeStage $Data $record 'Prepared';$prepared=$true
        if($Checkpoint){$null=& $Checkpoint 'Prepared'}
        & $Service.Disable;& $Service.Stop
        $lease=[IO.FileStream]::new((Get-WorkerChild $Data 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        if($Checkpoint){$null=& $Checkpoint 'Stopped'}
        New-WorkerSnapshot $Data $record
        if($Checkpoint){$null=& $Checkpoint 'BackupCopied'}
        Set-WorkerUpgradeStage $Data $record 'BackedUp'
        if($Checkpoint){$null=& $Checkpoint 'BackedUp'}
        $paths=Get-WorkerUpgradePaths $Program $record
        Copy-WorkerPackage $Source $paths.Stage $Program
        $null=Test-WorkerPackage $paths.Stage $Publisher -Development:$Development
        Set-WorkerUpgradeStage $Data $record 'Staged'
        if($Checkpoint){$null=& $Checkpoint 'Staged'}
        Switch-WorkerPackage $Data $Program $record $Checkpoint
        if($Checkpoint){$null=& $Checkpoint 'Switched'}
        $lease.Dispose();$lease=$null
        & $Service.Enable;& $Service.Start
        Wait-WorkerHealthy $Service
        $current=Read-WorkerUpgrade $Data
        if($current.Id -ne $record.Id -or $current.Stage -ne 'Healthy'){throw '候选维护代次未就绪。'}
        if($Checkpoint){$null=& $Checkpoint 'CandidateHealthy'}
        Set-WorkerUpgradeStage $Data $current 'Committed'
    }catch{
        if($lease){$lease.Dispose();$lease=$null}
        if($prepared){Stop-WorkerForRepair $Data $Service}
        throw
    }finally{if($lease){$lease.Dispose()};$operation.Dispose()}
}
function Invoke-WorkerRecoveryTransaction {
    param([string]$Data,[string]$Program,[hashtable]$Service,
        [ValidateSet('RetryCandidate','RestorePrevious','ResumeConfirmed')][string]$Action,
        [string]$Publisher,[switch]$Development,[scriptblock]$Checkpoint=$null)
    $operation=[IO.FileStream]::new((Get-WorkerChild $Data 'maintenance.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $lease=$null;$quiescing=$false
    try{
        $record=Read-WorkerUpgrade $Data
        if(-not $record){throw '没有可恢复的Worker维护日志。'}
        $terminal=$record.Stage -in @('Committed','RolledBack')
        if(($Action -eq 'ResumeConfirmed') -ne $terminal){throw '已确认事务只能恢复启动；未解决事务必须继续候选或安全回退。'}
        # 禁止危险回退的请求在停止正常候选之前即拒绝。
        if($Action -eq 'RestorePrevious' -and $record.NewFactsPossible){throw '可能已有新事实，拒绝回退。'}
        $quiescing=$true;& $Service.Disable;& $Service.Stop
        $lease=[IO.FileStream]::new((Get-WorkerChild $Data 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $record=Read-WorkerUpgrade $Data
        $paths=Get-WorkerUpgradePaths $Program $record
        switch($Action){
            'RestorePrevious'{
                $old=if(Test-Path -LiteralPath $paths.Previous){$paths.Previous}else{$paths.Active}
                $null=Test-WorkerPackage $old $Publisher -Development:$Development
                Restore-WorkerPrevious $Data $Program $record $Checkpoint
            }
            'RetryCandidate'{
                Test-WorkerSnapshot $Data $record
                if(-not (Test-Path -LiteralPath $paths.Active) -and (Test-Path -LiteralPath $paths.Stage)){
                    $null=Test-WorkerPackage $paths.Stage $Publisher -Development:$Development
                    if((Get-FileHash (Join-Path $paths.Stage 'release.json')).Hash -ne $record.CandidateReleaseHash){throw '暂存发行不匹配。'}
                    [IO.Directory]::Move($paths.Stage,$paths.Active)
                }
                $null=Test-WorkerPackage $paths.Active $Publisher -Development:$Development
                if((Get-FileHash (Join-Path $paths.Active 'release.json')).Hash -ne $record.CandidateReleaseHash){throw '活动程序不是原候选。'}
                Set-WorkerUpgradeStage $Data $record 'Switched'
            }
            'ResumeConfirmed'{
                $null=Test-WorkerPackage $paths.Active $Publisher -Development:$Development
                $expected=if($record.Stage -eq 'RolledBack'){$record.PreviousReleaseHash}else{$record.CandidateReleaseHash}
                if((Get-FileHash (Join-Path $paths.Active 'release.json')).Hash -ne $expected){throw '活动发行不是已确认代次。'}
            }
        }
        $lease.Dispose();$lease=$null
        & $Service.Enable;& $Service.Start
        Wait-WorkerHealthy $Service
        $current=Read-WorkerUpgrade $Data
        $expectedStage=if($Action -eq 'RetryCandidate'){'Healthy'}elseif($Action -eq 'RestorePrevious'){'RolledBack'}else{$record.Stage}
        if($current.Id -ne $record.Id -or $current.Stage -ne $expectedStage){throw '恢复后维护代次不一致。'}
        if($Action -eq 'RetryCandidate'){Set-WorkerUpgradeStage $Data $current 'Committed'}
    }catch{
        if($lease){$lease.Dispose();$lease=$null}
        if($quiescing){Stop-WorkerForRepair $Data $Service}
        throw
    }finally{if($lease){$lease.Dispose()};$operation.Dispose()}
}
Export-ModuleMember -Function Assert-WorkerServiceRegistration,New-WorkerServiceOperations,Invoke-WorkerUpgradeTransaction,Invoke-WorkerRecoveryTransaction
