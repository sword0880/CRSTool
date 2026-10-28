#Requires -Version 7.0
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$PackageDirectory,[string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
Import-Module (Join-Path $RepositoryRoot 'tools/background/WorkerMaintenance.psm1') -Force
Import-Module (Join-Path $RepositoryRoot 'tools/background/WorkerUpgradeTransaction.psm1') -Force
$source=[IO.Path]::GetFullPath($PackageDirectory)
$release=Test-WorkerPackage $source '' -Development
if(@($release.storageMigrations).Count -ne 1 -or $release.storageMigrations[0].pluginId -ne 'crs.national-team' -or $release.storageMigrations[0].migrationId -ne 'national-team-plaintext-v1'){throw '需要显式国家队迁移候选包。'}
$runId=[Guid]::NewGuid().ToString('N');$name='CRSStorageMigrationTest'+$runId
$root=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot ('.test_tmp-storage-scm/'+$runId)))
$boundary=[IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.test_tmp-storage-scm'))+[IO.Path]::DirectorySeparatorChar
if(-not $root.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or $name -notmatch '^CRSStorageMigrationTest[a-f0-9]{32}$'){throw '测试所有权边界无效。'}
$program=Join-Path $root 'program';$data=Join-Path $root 'data';$pipe='CRS.StorageMigration.'+$runId
$background=Join-Path $RepositoryRoot 'tests/CRS.Tests.Background/bin/Release/net10.0-windows/CRS.Tests.Background.dll'
$reader=Join-Path $RepositoryRoot 'tests/CRS.Tests.WorkerEtf/bin/Release/net10.0-windows10.0.19041.0/CRS.Tests.WorkerEtf.dll'
$fixture=Join-Path (Split-Path $reader -Parent) 'CRS.Worker.EtfFixture.dll'
foreach($path in @($background,$reader,$fixture)){if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw '请先构建Release测试项目。'}}
$formal=Get-CimInstance Win32_Service -Filter "Name='CRSBackgroundService'"
$created=$false;$script:passed=0;$record=$null
function Check([bool]$Value,[string]$Label){if(-not $Value){throw $Label};$script:passed++;Write-Output "PASS 国家队SCM迁移：$Label"}
function Protect-TestTree([string]$Path,[bool]$Writable){
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl',$inherit,'None','Allow'))}
    $rights=if($Writable){'FullControl'}else{'ReadAndExecute'}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-19'),$rights,$inherit,'None','Allow'));Set-Acl -LiteralPath $Path -AclObject $acl
}
function Plain([string]$Path){$stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));try{$bytes=[byte[]]::new(16);return $stream.Read($bytes,0,16) -eq 16 -and [Text.Encoding]::ASCII.GetString($bytes) -eq ('SQLite format 3'+[char]0)}finally{$stream.Dispose()}}
try{
    Protect-TestTree $root $false;Copy-WorkerPackage $source $program $root;Protect-TestTree $data $true
    # 只有测试自有旧版本装配具名合成入口；候选包完全不改动。
    $plugin=Join-Path $program 'Plugins/crs.national-team/2.0.0';Copy-Item -LiteralPath $fixture -Destination $plugin
    $manifestPath=Join-Path $plugin 'plugin.json';$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $manifest.backgroundEntry=@{assembly='CRS.Worker.EtfFixture.dll';type='CRS.Worker.EtfFixture.LegacyStorageSeedEntry'};Write-WorkerJson $manifestPath $manifest
    $index=@(Get-Content -LiteralPath (Join-Path $program 'trusted-background-plugins.json') -Raw | ConvertFrom-Json -AsHashtable)
    $package=@($index | Where-Object id -eq 'crs.national-team')[0];$package.files=@{}
    foreach($file in Get-WorkerFiles $plugin){$package.files[[IO.Path]::GetRelativePath($plugin,$file.FullName).Replace('\','/')]=(Get-FileHash -LiteralPath $file.FullName).Hash}
    Write-WorkerJson (Join-Path $program 'trusted-background-plugins.json') $index
    $bootstrap=Get-Content -LiteralPath (Join-Path $program 'release.json') -Raw | ConvertFrom-Json -AsHashtable;$bootstrap.Remove('storageMigrations');$bootstrap.files=@{}
    foreach($file in Get-WorkerFiles $program){$relative=[IO.Path]::GetRelativePath($program,$file.FullName).Replace('\','/');if($relative -notin @('release.json','release.json.p7s')){$bootstrap.files[$relative]=(Get-FileHash -LiteralPath $file.FullName).Hash}}
    Write-WorkerJson (Join-Path $program 'release.json') $bootstrap;$null=Test-WorkerPackage $program '' -Development
    $binary='"'+(Join-Path $program 'CRS.Worker.Service.exe')+'" --console --data "'+$data+'" --pipe "'+$pipe+'"'
    if(Get-Service -Name $name -ErrorAction SilentlyContinue){throw '测试服务名称冲突。'}
    New-Service -Name $name -DisplayName $name -BinaryPathName $binary -StartupType Automatic -Credential ([PSCredential]::new('NT AUTHORITY\LocalService',[Security.SecureString]::new())) | Out-Null;$created=$true
    $probe={& dotnet $background --isolated-health-probe $pipe | Out-Null;return $LASTEXITCODE -eq 0}.GetNewClosure();$service=New-WorkerServiceOperations $name $program $probe
    & $service.Start;$deadline=[DateTime]::UtcNow.AddSeconds(90)
    while(-not (& $probe)){if([DateTime]::UtcNow -gt $deadline){throw 'LocalService合成旧库服务未就绪。'};Start-Sleep -Milliseconds 250}
    $registered=Get-CimInstance Win32_Service -Filter "Name='$name'"
    Check ($registered.State -eq 'Running' -and $registered.StartName -eq 'NT AUTHORITY\LocalService' -and $registered.PathName -eq $binary) '由真实LocalService身份创建独立密钥和合成旧资料'
    $keyHash=(Get-FileHash -LiteralPath (Join-Path $data 'service-key.dpapi')).Hash
    $market=Join-Path $data 'plugins/crs.national-team/data/market.db';$policy=Join-Path $data 'plugins/crs.national-team/data/source-policy.db'
    Check (-not (Plain $market) -and -not (Plain $policy)) '维护前两份资料确为旧加密格式'
    $old=@{'data/market.db'=(Get-FileHash -LiteralPath $market).Hash;'data/source-policy.db'=(Get-FileHash -LiteralPath $policy).Hash}
    $hit=@{Value=$false};$interrupt={param($point)if($point -eq 'CandidateHealthy'){$hit.Value=$true;throw 'synthetic-after-storage-migration'}}.GetNewClosure()
    try{Invoke-WorkerUpgradeTransaction -Data $data -Program $program -Source $source -Service $service -Development -Checkpoint $interrupt;throw '没有到达维护后中断点。'}catch{if(-not $hit.Value -or $_.Exception.Message -notmatch 'synthetic-after-storage-migration'){throw}}
    $record=Read-WorkerUpgrade $data
    Check ($record.Stage -eq 'NeedsManualRepair' -and $record.NewFactsPossible -and $record.StorageMigrationReceiptHash -match '^[A-Fa-f0-9]{64}$') '迁移和插件启动后注入中断，保留风险及独立回执'
    $refused=$false;try{Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action RestorePrevious -Development}catch{$refused=$_.Exception.Message -match '新事实'}
    Check $refused '有新事实风险时拒绝把旧加密资料覆盖回来'
    Invoke-WorkerRecoveryTransaction -Data $data -Program $program -Service $service -Action RetryCandidate -Development
    $record=Read-WorkerUpgrade $data
    Check ($record.Stage -eq 'Committed' -and (Plain $market) -and (Plain $policy)) '同代次恢复由真实SCM完成迁移确认和Committed'
    Check ((Get-FileHash -LiteralPath (Join-Path $data 'service-key.dpapi')).Hash -eq $keyHash -and -not (Plain (Join-Path $data 'scheduler-config.db'))) 'LocalService密钥原文件保留，Worker管理存储仍加密'
    $preserved=$true;foreach($relative in $old.Keys){foreach($path in @((Join-Path $data ('plugins/crs.national-team/storage-migrations/'+$record.Id+'/original/'+$relative)),(Join-Path $data ('maintenance/'+$record.Id+'/snapshot/plugins/crs.national-team/'+$relative)))){if((Get-FileHash -LiteralPath $path).Hash -ne $old[$relative]){$preserved=$false}}}
    Check $preserved '原加密库归档和完整停机快照的摘要均保持一致'
    & dotnet $reader --storage-migration-scm-read $pipe (Join-Path $data 'plugins/crs.national-team/migration-fixture-seed.json') (Join-Path $root 'client-readback.json')
    if($LASTEXITCODE -ne 0){throw '真实SCM迁移后客户端回读失败。'}
    Check $true '生产插件经真实公开管道回读24条配置、两只活动ETF和原份额'
    $formalAfter=Get-CimInstance Win32_Service -Filter "Name='CRSBackgroundService'"
    if($formal){Check ($formalAfter.ProcessId -eq $formal.ProcessId -and $formalAfter.PathName -eq $formal.PathName -and $formalAfter.State -eq $formal.State) '正式服务PID、路径和状态未改变'}
}finally{
    if($created -and $name -match '^CRSStorageMigrationTest[a-f0-9]{32}$'){
        Set-Service -Name $name -StartupType Disabled;$owned=Get-Service -Name $name
        try{if($owned.Status -ne 'Stopped'){Stop-Service -Name $name};$owned.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(35))}finally{$owned.Dispose()}
        & sc.exe delete $name | Out-Null;if($LASTEXITCODE -ne 0){throw '仅本次测试服务注册清理失败。'}
    }
}
Write-WorkerJson (Join-Path $root 'acceptance.json') @{Passed=$script:passed;Installed=$false;Source='IsolatedLocalServiceScmProductionMigration';LocalServiceVerified=$true;FormalDataMigrated=$false;OperationId=$record.Id;TestServiceRemoved=$true}
Write-Output "国家队LocalService迁移通过：$($script:passed)项；测试服务已移除；现场：$root"
