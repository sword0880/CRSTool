#Requires -Version 7.0
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$PackageDirectory,[string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $RepositoryRoot 'tools/background/WorkerMaintenance.psm1') -Force
Import-Module (Join-Path $RepositoryRoot 'tools/background/WorkerUpgradeTransaction.psm1') -Force
$runId=[Guid]::NewGuid().ToString('N')
$name='CRSWorkerUpgradeTest'+$runId
$root=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot ('.test_tmp-worker-scm/'+$runId)))
$expectedRoot=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.test_tmp-worker-scm'))+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($expectedRoot,[StringComparison]::OrdinalIgnoreCase) -or $name -notmatch '^CRSWorkerUpgradeTest[a-f0-9]{32}$'){throw '测试服务/目录标识无效。'}
$program=Join-Path $root 'program';$data=Join-Path $root 'data'
$pipe='CRS.Worker.ScmTest.'+$runId
$assembly=Join-Path $RepositoryRoot 'tests/CRS.Tests.Background/bin/Release/net10.0-windows/CRS.Tests.Background.dll'
if(-not (Test-Path -LiteralPath $assembly)){throw '请先构建Release后台测试项目。'}
$created=$false;$script:passed=0
$formal=Get-CimInstance Win32_Service -Filter "Name='CRSBackgroundService'"
function Check([bool]$Value,[string]$Label){if(-not $Value){throw $Label};$script:passed++;Write-Output "PASS SCM：$Label"}
function Assert-LocalServiceDirectory([string]$Path,[bool]$Writable){
    # 只配置新建测试树；程序只读，数据允许LocalService；不改正式ProgramData或ProgramFiles。
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl',$inherit,'None','Allow'))
    }
    $rights=if($Writable){'FullControl'}else{'ReadAndExecute'}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-19'),$rights,$inherit,'None','Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function New-Candidate([string]$Version){
    $target=Join-Path $root ('candidate-'+$Version)
    Copy-WorkerPackage ([IO.Path]::GetFullPath($PackageDirectory)) $target $program
    $release=Get-Content -Raw (Join-Path $target 'release.json') | ConvertFrom-Json -AsHashtable
    # 相同可执行构建、不同合成发行指纹：验证维护协议，不冒充真实Schema升级验收。
    $release.version=$Version;$release.upgradeFromVersions=@('2.0.0','2.0.1','2.0.2')
    Write-WorkerJson (Join-Path $target 'release.json') $release
    return $target
}
function Assert-Fact([string]$Id){
    & dotnet $assembly --isolated-fact-read $pipe $Id
    if($LASTEXITCODE -ne 0){throw '服务升级/恢复后丢失已提交测试事实。'}
}
try{
    Assert-LocalServiceDirectory $root $false
    Copy-WorkerPackage ([IO.Path]::GetFullPath($PackageDirectory)) $program $root
    Assert-LocalServiceDirectory $data $true
    $source=New-Candidate '2.0.1'
    $binary='"'+(Join-Path $program 'CRS.Worker.Service.exe')+'" --console --data "'+$data+'" --plugins "'+(Join-Path $program 'Plugins')+'" --pipe "'+$pipe+'"'
    if(Get-Service -Name $name -ErrorAction SilentlyContinue){throw '测试服务名称发生冲突。'}
    New-Service -Name $name -DisplayName $name -BinaryPathName $binary -StartupType Automatic -Credential ([PSCredential]::new('NT AUTHORITY\LocalService',[Security.SecureString]::new())) | Out-Null
    $created=$true
    $probe={& dotnet $assembly --isolated-health-probe $pipe;return $LASTEXITCODE -eq 0}.GetNewClosure()
    $service=New-WorkerServiceOperations $name $program $probe
    & $service.Start
    $deadline=[DateTime]::UtcNow.AddSeconds(35)
    while(-not (& $probe)){if([DateTime]::UtcNow -gt $deadline){throw '隔离SCM服务未就绪。'};Start-Sleep -Milliseconds 250}
    $registered=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Check ($registered.State -eq 'Running' -and $registered.StartName -eq 'NT AUTHORITY\LocalService' -and $registered.PathName -eq $binary) '真实SCM以LocalService启动独立Worker，无正式端点或数据目录复用'
    $keyHash=(Get-FileHash (Join-Path $data 'service-key.dpapi')).Hash
    Start-Sleep -Milliseconds 2200
    $oldFact=(& dotnet $assembly --isolated-fact-create $pipe | Select-Object -Last 1)
    if($LASTEXITCODE -ne 0 -or $oldFact -notmatch '^[a-fA-F0-9]{32}$'){throw 'SCM基线事实创建失败。'}
    $hit=@{Value=$false}
    $failBeforeStart={param($point) if($point -eq 'PreviousMoved'){$hit.Value=$true;throw 'synthetic-before-candidate-start'}}.GetNewClosure()
    try{Invoke-WorkerUpgradeTransaction -Data $data -Program $program -Source $source -Service $service -Development -Checkpoint $failBeforeStart;throw '未注入切换中断。'}
    catch{if(-not $hit.Value -or $_.Exception.Message -notmatch 'synthetic-before-candidate-start'){throw}}
    $record=Read-WorkerUpgrade $data;$registered=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Check ($record.Stage -eq 'NeedsManualRepair' -and -not $record.NewFactsPossible -and $registered.State -eq 'Stopped' -and $registered.StartMode -eq 'Disabled') '程序切换中断后真实服务停止且禁用，日志保留无新事实状态'
    Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action RestorePrevious -Development
    Assert-Fact $oldFact
    Check ((Read-WorkerUpgrade $data).Stage -eq 'RolledBack' -and (Get-FileHash (Join-Path $data 'service-key.dpapi')).Hash -eq $keyHash) '切换中断后SCM恢复旧程序，原事实及机器密钥保留'
    Invoke-WorkerUpgradeTransaction -Data $data -Program $program -Source $source -Service $service -Development
    Assert-Fact $oldFact
    Check ((Read-WorkerUpgrade $data).Stage -eq 'Committed' -and (Get-CimInstance Win32_Service -Filter "Name='$name'").State -eq 'Running') '同一生产协调函数完成真实SCM停机、备份、切换、健康及Commit'
    $source2=New-Candidate '2.0.2';$afterOpen=@{Hit=$false;Fact=$null}
    $failAfterOpen={param($point) if($point -eq 'CandidateHealthy'){
        $afterOpen.Hit=$true
        Start-Sleep -Milliseconds 2200
        $afterOpen.Fact=(& dotnet $assembly --isolated-fact-create $pipe | Select-Object -Last 1)
        if($LASTEXITCODE -ne 0){throw '候选事实创建失败。'}
        throw 'synthetic-after-candidate-write'
    }}.GetNewClosure()
    try{Invoke-WorkerUpgradeTransaction -Data $data -Program $program -Source $source2 -Service $service -Development -Checkpoint $failAfterOpen;throw '未注入候选写入后故障。'}
    catch{if(-not $afterOpen.Hit -or $_.Exception.Message -notmatch 'synthetic-after-candidate-write'){throw}}
    Check ((Read-WorkerUpgrade $data).NewFactsPossible -and (Read-WorkerUpgrade $data).Stage -eq 'NeedsManualRepair') '真实候选提交新执行后失败，最新风险标记不会被控制脚本覆盖'
    $refused=$false
    try{Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action RestorePrevious -Development}catch{$refused=$_.Exception.Message -match '新事实'}
    Check $refused '真实候选有新事实时拒绝恢复旧版本'
    Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action RetryCandidate -Development
    Assert-Fact $oldFact;Assert-Fact $afterOpen.Fact
    Check ((Read-WorkerUpgrade $data).Stage -eq 'Committed') '继续原候选后旧事实和失败前的新事实都可经真实管道读取'
    & $service.Disable;& $service.Stop
    Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action ResumeConfirmed -Development
    Assert-Fact $afterOpen.Fact
    Check ((Read-WorkerUpgrade $data).Stage -eq 'Committed' -and (Get-FileHash (Join-Path $data 'service-key.dpapi')).Hash -eq $keyHash) '已确认版本可恢复真实SCM启动，不回退数据库或更换密钥'
    & $service.Disable;& $service.Stop
    $releasePath=Join-Path $program 'release.json';$releaseBytes=[IO.File]::ReadAllBytes($releasePath)
    $dbHash=(Get-FileHash (Join-Path $data 'scheduler-config.db')).Hash
    Add-Content -LiteralPath $releasePath -Value ' '
    $refused=$false
    try{Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action ResumeConfirmed -Development}catch{$refused=$_.Exception.Message -match '已确认代次'}
    Check ($refused -and (Get-FileHash (Join-Path $data 'scheduler-config.db')).Hash -eq $dbHash) '发行指纹不同的恢复启动被拒绝，数据库未被打开改写'
    [IO.File]::WriteAllBytes($releasePath,$releaseBytes)
    Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action ResumeConfirmed -Development
    Assert-Fact $oldFact;Assert-Fact $afterOpen.Fact
    $formalAfter=Get-CimInstance Win32_Service -Filter "Name='CRSBackgroundService'"
    if($formal){Check ($formalAfter.ProcessId -eq $formal.ProcessId -and $formalAfter.PathName -eq $formal.PathName -and $formalAfter.State -eq $formal.State) '正式后台服务PID、路径及状态未改变'}
}finally{
    # 只清理本次New-Service成功创建的唯一名称；保留测试数据用于复核。
    if($created -and $name -match '^CRSWorkerUpgradeTest[a-f0-9]{32}$'){
        Set-Service -Name $name -StartupType Disabled
        $owned=Get-Service -Name $name
        try{if($owned.Status -ne 'Stopped'){Stop-Service -Name $name};$owned.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))}finally{$owned.Dispose()}
        & sc.exe delete $name | Out-Null
        if($LASTEXITCODE -ne 0){throw '测试服务注册清理失败。'}
    }
}
Write-Output "SCM升级恢复通过：$($script:passed)项；测试服务已移除；隔离现场：$root"
