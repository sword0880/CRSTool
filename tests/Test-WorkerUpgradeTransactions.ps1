#Requires -Version 7.0
param([string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $RepositoryRoot 'tools/background/WorkerMaintenance.psm1') -Force
Import-Module (Join-Path $RepositoryRoot 'tools/background/WorkerUpgradeTransaction.psm1') -Force

$root=Join-Path ([IO.Path]::GetTempPath()) ('CRS.Worker.TransactionTests-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:passed=0
$script:completed=$false
function Check([bool]$Value,[string]$Name){
    if(-not $Value){throw $Name}
    $script:passed++
    Write-Output "PASS Worker transaction: $Name"
}
function New-TestPackage([string]$Path,[string]$Version,[string[]]$UpgradeFrom=@('2.0.0')){
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path 'CRS.Worker.Service.exe'),"synthetic-worker-$Version")
    [IO.File]::WriteAllText((Join-Path $Path 'trusted-background-plugins.json'),'[]')
    $files=@{}
    foreach($file in Get-ChildItem -LiteralPath $Path -File){
        $files[$file.Name]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    Write-WorkerJson (Join-Path $Path 'release.json') @{
        format=1;product='CRS.Worker.Service';version=$Version;files=$files
        maintenanceFormat=1;upgradeFromVersions=$UpgradeFrom
    }
}
function New-Fixture([string]$Name){
    $caseRoot=Join-Path $root $Name
    $program=Join-Path $caseRoot 'program'
    $candidate=Join-Path $caseRoot 'candidate'
    $data=Join-Path $caseRoot 'data'
    New-Item -ItemType Directory -Path $caseRoot,$data,(Join-Path $data 'plugins/p/raw') -Force | Out-Null
    New-TestPackage $program '2.0.0' @('1.9.0')
    New-TestPackage $candidate '2.0.1' @('2.0.0')
    [IO.File]::WriteAllText((Join-Path $data 'service-key.dpapi'),'synthetic-machine-protected-key')
    [IO.File]::WriteAllText((Join-Path $data 'scheduler-config.db'),'synthetic-encrypted-database-before-upgrade')
    [IO.File]::WriteAllText((Join-Path $data 'plugins/p/raw/review.raw'),'synthetic-reviewed-source-evidence')
    return @{
        Root=$caseRoot;Program=$program;Candidate=$candidate;Data=$data
        PreviousHash=(Get-FileHash (Join-Path $program 'release.json') -Algorithm SHA256).Hash
        CandidateHash=(Get-FileHash (Join-Path $candidate 'release.json') -Algorithm SHA256).Hash
        KeyHash=(Get-FileHash (Join-Path $data 'service-key.dpapi') -Algorithm SHA256).Hash
        DatabaseHash=(Get-FileHash (Join-Path $data 'scheduler-config.db') -Algorithm SHA256).Hash
        EvidenceHash=(Get-FileHash (Join-Path $data 'plugins/p/raw/review.raw') -Algorithm SHA256).Hash
    }
}
function New-MockService([string]$Data){
    $state=@{Disabled=$false;Stopped=$false;Enabled=$true;Started=$false;Healthy=$true;FailStart=$false;FailHealth=$false}
    $disable={ $state.Disabled=$true }.GetNewClosure()
    $stop={ $state.Stopped=$true }.GetNewClosure()
    $enable={ $state.Enabled=$true;$state.Disabled=$false }.GetNewClosure()
    $start={
        if($state.FailStart){throw 'synthetic-start-failure'}
        $record=Read-WorkerUpgrade $Data
        if($record -and $record.Stage -eq 'Switched'){
            $database=Join-Path $Data 'scheduler-config.db'
            $current=[IO.File]::ReadAllText($database)
            if(-not $current.Contains('candidate-fact')){
                [IO.File]::AppendAllText($database,[Environment]::NewLine+'candidate-fact')
            }
            $record.NewFactsPossible=$true
            Set-WorkerUpgradeStage $Data $record 'Starting'
            $record=Read-WorkerUpgrade $Data
            Set-WorkerUpgradeStage $Data $record 'Healthy'
        }
        $state.Started=$true
        $state.Stopped=$false
    }.GetNewClosure()
    $health={
        if($state.FailHealth){throw 'synthetic-health-probe-failure'}
        return [bool]$state.Healthy
    }.GetNewClosure()
    return @{Disable=$disable;Stop=$stop;Enable=$enable;Start=$start;Health=$health;State=$state}
}
function Assert-OriginalData([hashtable]$Fixture){
    Check ((Get-FileHash (Join-Path $Fixture.Data 'service-key.dpapi') -Algorithm SHA256).Hash -eq $Fixture.KeyHash) '失败恢复不替换机器密钥文件'
    Check ((Get-FileHash (Join-Path $Fixture.Data 'scheduler-config.db') -Algorithm SHA256).Hash -eq $Fixture.DatabaseHash) '无新事实回退不覆盖数据库'
    Check ((Get-FileHash (Join-Path $Fixture.Data 'plugins/p/raw/review.raw') -Algorithm SHA256).Hash -eq $Fixture.EvidenceHash) '失败恢复保留人工审核原件'
}
function Run-CheckpointFailure([string]$Point){
    $fixture=New-Fixture ('checkpoint-'+$Point)
    $service=New-MockService $fixture.Data
    $target=$Point
    $checkpoint={param($stage) if($stage -eq $target){throw "injected:$target"}}.GetNewClosure()
    $expected="injected:$Point"
    try{
        Invoke-WorkerUpgradeTransaction -Data $fixture.Data -Program $fixture.Program -Source $fixture.Candidate -Service $service -Development -Checkpoint $checkpoint
        throw "Checkpoint未触发：$Point"
    }catch{
        if($_.Exception.Message -ne $expected){throw}
    }
    $record=Read-WorkerUpgrade $fixture.Data
    Check ($record.Stage -eq 'NeedsManualRepair') "$Point 中断进入显式维护状态"
    Check ($service.State.Disabled -and $service.State.Stopped) "$Point 中断后服务操作被禁用并停止"
    $expectedNewFacts=($Point -eq 'CandidateHealthy')
    Check ($record.NewFactsPossible -eq $expectedNewFacts) "$Point 中断保留正确的新事实风险标记"
    if(-not $expectedNewFacts){
        Assert-OriginalData $fixture
        Invoke-WorkerRecoveryTransaction -Data $fixture.Data -Program $fixture.Program -Service $service -Action RestorePrevious -Development
        $rolled=Read-WorkerUpgrade $fixture.Data
        Check ($rolled.Stage -eq 'RolledBack' -and -not $rolled.NewFactsPossible) "$Point 可恢复到已核验的旧程序代次"
        Check ((Get-FileHash (Join-Path $fixture.Program 'release.json') -Algorithm SHA256).Hash -eq $fixture.PreviousHash) "$Point 回退后的活动程序指纹等于旧代次"
        Assert-OriginalData $fixture
    }else{
        $blocked=$false
        try{Invoke-WorkerRecoveryTransaction -Data $fixture.Data -Program $fixture.Program -Service $service -Action RestorePrevious -Development}
        catch{$blocked=$_.Exception.Message -match '新事实'}
        Check $blocked '候选可能写入新事实后拒绝恢复旧程序'
        $database=Join-Path $fixture.Data 'scheduler-config.db'
        Check ([IO.File]::ReadAllText($database).Contains('candidate-fact')) '候选写入事实在维护状态下保留'
        $record=Read-WorkerUpgrade $fixture.Data
        $snapshotFile=Join-Path $fixture.Data ('maintenance/'+$record.Id+'/snapshot/plugins/p/raw/review.raw')
        $snapshotOriginal=[IO.File]::ReadAllBytes($snapshotFile)
        [IO.File]::AppendAllText($snapshotFile,'tampered-snapshot')
        $blocked=$false
        try{Invoke-WorkerRecoveryTransaction -Data $fixture.Data -Program $fixture.Program -Service $service -Action RetryCandidate -Development}
        catch{$blocked=$true}
        Check $blocked '候选恢复前检测被篡改的备份原件'
        Check ($service.State.Disabled -and $service.State.Stopped) '备份核验失败后继续保持服务停止'
        Check ([IO.File]::ReadAllText($database).Contains('candidate-fact')) '备份核验失败不回滚或覆盖活动数据'
        [IO.File]::WriteAllBytes($snapshotFile,$snapshotOriginal)
        $service.State.FailHealth=$false
        Invoke-WorkerRecoveryTransaction -Data $fixture.Data -Program $fixture.Program -Service $service -Action RetryCandidate -Development
        $committed=Read-WorkerUpgrade $fixture.Data
        Check ($committed.Stage -eq 'Committed' -and $committed.NewFactsPossible) '恢复原候选后完成显式Commit'
        Check (([IO.File]::ReadAllText($database).Split('candidate-fact').Length-1) -eq 1) '重试候选不重复写入模拟事实'
        Check ((Get-FileHash (Join-Path $fixture.Data 'service-key.dpapi') -Algorithm SHA256).Hash -eq $fixture.KeyHash) '继续候选不更换密钥文件'
    }
}
try{
    # 包验证/兼容性失败必须发生在停止服务和创建维护事务之前。
    $bad=New-Fixture 'preflight-tamper'
    $service=New-MockService $bad.Data
    [IO.File]::AppendAllText((Join-Path $bad.Candidate 'CRS.Worker.Service.exe'),'-tampered')
    $rejected=$false
    try{Invoke-WorkerUpgradeTransaction -Data $bad.Data -Program $bad.Program -Source $bad.Candidate -Service $service -Development}
    catch{$rejected=$true}
    Check $rejected '候选文件哈希不匹配时拒绝升级'
    Check (-not $service.State.Disabled -and -not $service.State.Stopped) '候选预检失败不停止现有服务'
    Check (-not (Test-Path (Join-Path $bad.Data 'maintenance/upgrade.json'))) '候选预检失败不创建虚假升级事务'
    Check ((Get-FileHash (Join-Path $bad.Data 'scheduler-config.db') -Algorithm SHA256).Hash -eq $bad.DatabaseHash) '候选预检失败不修改活动数据库'

    $compat=New-Fixture 'preflight-incompatible'
    Remove-Item -LiteralPath $compat.Candidate -Recurse -Force
    New-TestPackage $compat.Candidate '2.0.1' @('1.8.0')
    $service=New-MockService $compat.Data
    $rejected=$false
    try{Invoke-WorkerUpgradeTransaction -Data $compat.Data -Program $compat.Program -Source $compat.Candidate -Service $service -Development}
    catch{$rejected=$true}
    Check $rejected '候选未声明兼容当前发行时拒绝升级'
    Check (-not $service.State.Disabled -and -not (Test-Path (Join-Path $compat.Data 'maintenance/upgrade.json'))) '兼容性预检失败不停止服务或创建事务'

    foreach($point in @('Prepared','Stopped','BackupCopied','BackedUp','Staged','PreviousMoved','CandidateMoved','Switched')){
        Run-CheckpointFailure $point
    }
    Run-CheckpointFailure 'CandidateHealthy'

    # 模拟候选进程启动失败：未跨越数据打开门禁时允许安全回退。
    $startFailure=New-Fixture 'candidate-start-failure'
    $service=New-MockService $startFailure.Data
    $service.State.FailStart=$true
    $failed=$false
    try{Invoke-WorkerUpgradeTransaction -Data $startFailure.Data -Program $startFailure.Program -Source $startFailure.Candidate -Service $service -Development}
    catch{$failed=$_.Exception.Message -eq 'synthetic-start-failure'}
    Check $failed '候选启动失败可被事务捕获'
    Check ((Read-WorkerUpgrade $startFailure.Data).Stage -eq 'NeedsManualRepair' -and -not (Read-WorkerUpgrade $startFailure.Data).NewFactsPossible) '候选打开数据前启动失败保留无新事实状态'
    $service.State.FailStart=$false
    Invoke-WorkerRecoveryTransaction -Data $startFailure.Data -Program $startFailure.Program -Service $service -Action RestorePrevious -Development
    Check ((Read-WorkerUpgrade $startFailure.Data).Stage -eq 'RolledBack') '候选启动失败后恢复旧程序'
    Assert-OriginalData $startFailure

    # 健康探针失败发生在候选已标记Starting/Healthy之后，必须保留风险并禁止旧代次回退。
    $healthFailure=New-Fixture 'candidate-health-failure'
    $service=New-MockService $healthFailure.Data
    $service.State.FailHealth=$true
    $failed=$false
    try{Invoke-WorkerUpgradeTransaction -Data $healthFailure.Data -Program $healthFailure.Program -Source $healthFailure.Candidate -Service $service -Development}
    catch{$failed=$_.Exception.Message -eq 'synthetic-health-probe-failure'}
    Check $failed '候选健康探针失败被事务捕获'
    $record=Read-WorkerUpgrade $healthFailure.Data
    Check ($record.Stage -eq 'NeedsManualRepair' -and $record.NewFactsPossible) '候选健康探针失败保留新事实风险'
    $blocked=$false
    try{Invoke-WorkerRecoveryTransaction -Data $healthFailure.Data -Program $healthFailure.Program -Service $service -Action RestorePrevious -Development}
    catch{$blocked=$_.Exception.Message -match '新事实'}
    Check $blocked '候选健康探针失败后拒绝覆盖旧数据库回退'
    $service.State.FailHealth=$false
    Invoke-WorkerRecoveryTransaction -Data $healthFailure.Data -Program $healthFailure.Program -Service $service -Action RetryCandidate -Development
    Check ((Read-WorkerUpgrade $healthFailure.Data).Stage -eq 'Committed') '修复健康探针后可继续原候选'
    Check ([IO.File]::ReadAllText((Join-Path $healthFailure.Data 'scheduler-config.db')).Contains('candidate-fact')) '健康探针恢复后保留候选新事实'

    # 正常路径也必须提交同一候选，并留下可复核备份。
    $success=New-Fixture 'successful-upgrade'
    $service=New-MockService $success.Data
    Invoke-WorkerUpgradeTransaction -Data $success.Data -Program $success.Program -Source $success.Candidate -Service $service -Development
    $record=Read-WorkerUpgrade $success.Data
    Check ($record.Stage -eq 'Committed' -and $record.NewFactsPossible) '正常升级完成显式Commit'
    Check ((Get-FileHash (Join-Path $success.Program 'release.json') -Algorithm SHA256).Hash -eq $success.CandidateHash) '正常升级活动程序指纹等于候选'
    Check (Test-Path (Get-WorkerUpgradePaths $success.Program $record).Previous) '正常升级保留旧程序代次'
    Test-WorkerSnapshot $success.Data $record
    Check ([IO.File]::ReadAllText((Join-Path $success.Data 'scheduler-config.db')).Contains('candidate-fact')) '正常升级保留候选事实'
    Check ((Get-FileHash (Join-Path $success.Data 'service-key.dpapi') -Algorithm SHA256).Hash -eq $success.KeyHash) '正常升级不替换机器密钥'
    $script:completed=$true
    Write-Output "Worker升级事务隔离测试通过：$script:passed 项；仅使用合成包与唯一临时目录，未创建或控制 Windows 服务。"
}finally{
    if($script:completed -and (Test-Path -LiteralPath $root)){
        $expectedPrefix=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
        $full=[IO.Path]::GetFullPath($root)
        if($full.StartsWith($expectedPrefix,[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $full -Leaf) -match '^CRS\.Worker\.TransactionTests-[a-f0-9]{32}$'){
            Remove-Item -LiteralPath $full -Recurse -Force
        }else{throw '拒绝清理测试目录：路径不符合本次唯一临时目录边界。'}
    }elseif(Test-Path -LiteralPath $root){
        Write-Warning "测试失败，保留隔离现场：$root"
    }
}
