param([string]$RepositoryRoot=(Split-Path -Parent $PSScriptRoot),[switch]$CheckStartupInterop)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $RepositoryRoot 'tools/background/WorkerMaintenance.psm1') -Force
$root=Join-Path $RepositoryRoot ('.test_tmp-worker-upgrade/module-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$script:passed=0
function Check([bool]$Value,[string]$Name){if(-not $Value){throw $Name};$script:passed++;Write-Output "PASS 维护：$Name"}
function Reject([scriptblock]$Work,[string]$Name){try{& $Work}catch{Check $true $Name;return};throw "未拒绝：$Name"}
function Package([string]$Path,[string]$Version){
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $Path 'CRS.Worker.Service.exe') -Value ('synthetic-binary-'+$Version)
    Set-Content -LiteralPath (Join-Path $Path 'trusted-background-plugins.json') -Value '[]'
    $files=@{};foreach($file in Get-ChildItem -LiteralPath $Path -File){$files[$file.Name]=(Get-FileHash -LiteralPath $file.FullName).Hash}
    Write-WorkerJson (Join-Path $Path 'release.json') @{format=1;product='CRS.Worker.Service';version=$Version;files=$files;maintenanceFormat=1;upgradeFromVersions=@('2.0.0')}
}
$program=Join-Path $root 'program';$candidate=Join-Path $root 'candidate';$data=Join-Path $root 'data'
Package $program '2.0.0';Package $candidate '2.0.1'
$null=Test-WorkerPackage $candidate '' -Development
Check $true '开发包必须显式豁免，完整文件核验通过'
Reject {Test-WorkerPackage $candidate ''} '正式维护拒绝无外部发布者的未签名包'
$entry=Join-Path $candidate 'CRS.Worker.Service.exe';$original=[IO.File]::ReadAllBytes($entry)
Add-Content -LiteralPath $entry -Value 'tampered'
Reject {Test-WorkerPackage $candidate '' -Development} '包内二进制篡改被拒绝'
[IO.File]::WriteAllBytes($entry,$original)
Reject {Get-WorkerChild $root '../escape'} '相对路径越界被拒绝'
Reject {Get-WorkerChild $root 'file:stream'} 'NTFS备用数据流路径被拒绝'
$linkTarget=Join-Path $root 'link-target';New-Item -ItemType Directory -Path $linkTarget | Out-Null
$link=Join-Path $root 'linked';New-Item -ItemType Junction -Path $link -Target $linkTarget | Out-Null
Reject {Get-WorkerChild $root 'linked/not-created/child.txt'} '缺失叶节点也逐级拒绝链接祖先'
Check ((Get-WorkerChild $root 'ordinary/not-created.txt') -eq (Join-Path $root 'ordinary/not-created.txt')) '普通未创建路径仍可用于后续安全写入'
# 归档在本次数据快照范围之外，须在枚举前裁剪；严格模式及活动业务路径仍拒绝链接。
$archiveScope=Join-Path $root 'archive-scope';New-Item -ItemType Directory -Path (Join-Path $archiveScope 'maintenance') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $archiveScope 'fact.raw') -Value 'synthetic-active-fact'
New-Item -ItemType Junction -Path (Join-Path $archiveScope 'maintenance/archive-link') -Target $linkTarget | Out-Null
Check (@(Get-WorkerFiles $archiveScope -ExcludeMaintenance).Count -eq 1) '业务枚举在进入旧维护归档前裁剪，不读取或计数归档内容'
Reject {Get-WorkerFiles $archiveScope} '程序包与快照的严格枚举不享受归档排除'
New-Item -ItemType Junction -Path (Join-Path $archiveScope 'active-link') -Target $linkTarget | Out-Null
Reject {Get-WorkerFiles $archiveScope -ExcludeMaintenance} '排除旧归档仍拒绝活动业务树中的链接'
$archiveRootLink=Join-Path $root 'archive-root-link';New-Item -ItemType Directory -Path $archiveRootLink | Out-Null
New-Item -ItemType Junction -Path (Join-Path $archiveRootLink 'maintenance') -Target $linkTarget | Out-Null
Reject {Get-WorkerFiles $archiveRootLink -ExcludeMaintenance} '归档根为链接时不能借排除模式绕过检查'
New-Item -ItemType Directory -Path (Join-Path $data 'maintenance'),(Join-Path $data 'plugins/p/raw') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $data 'service-key.dpapi') -Value 'synthetic-protected-key'
Set-Content -LiteralPath (Join-Path $data 'scheduler-config.db') -Value 'synthetic-encrypted-database'
Set-Content -LiteralPath (Join-Path $data 'scheduler-config.db-wal') -Value 'synthetic-wal'
Set-Content -LiteralPath (Join-Path $data 'plugins/p/raw/review.raw') -Value 'synthetic-manual-review'
$record=@{Format=1;Id=[Guid]::NewGuid().ToString('N');PreviousReleaseHash=(Get-FileHash (Join-Path $program 'release.json')).Hash;
    CandidateReleaseHash=(Get-FileHash (Join-Path $candidate 'release.json')).Hash;PreviousVersion='2.0.0';CandidateVersion='2.0.1';
    Stage='Prepared';NewFactsPossible=$false;UpdatedUtc=[DateTimeOffset]::UtcNow.ToString('O');BackupManifestHash=$null}
Set-WorkerUpgradeStage $data $record 'Prepared'
$lease=[IO.FileStream]::new((Join-Path $data 'owner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{New-WorkerSnapshot $data $record}finally{$lease.Dispose()}
Set-WorkerUpgradeStage $data $record 'BackedUp'
$snapshot=Join-Path $data ('maintenance/'+$record.Id+'/snapshot')
Check ((Test-Path (Join-Path $snapshot 'scheduler-config.db-wal')) -and (Test-Path (Join-Path $snapshot 'plugins/p/raw/review.raw')) -and -not (Test-Path (Join-Path $snapshot 'owner.lock'))) '停机快照包含WAL和人工原件，排除独占运行租约'
Reject {New-WorkerSnapshot $data $record} '同事务快照不允许覆盖'
$raw=Join-Path $snapshot 'plugins/p/raw/review.raw';$rawOriginal=[IO.File]::ReadAllBytes($raw)
Add-Content -LiteralPath $raw -Value 'tamper'
Reject {Test-WorkerSnapshot $data $record} '备份中的人工证据篡改被拒绝'
[IO.File]::WriteAllBytes($raw,$rawOriginal)
$paths=Get-WorkerUpgradePaths $program $record
Copy-WorkerPackage $candidate $paths.Stage $program
$null=Test-WorkerPackage $paths.Stage '' -Development
Set-WorkerUpgradeStage $data $record 'Staged'
Switch-WorkerPackage $data $program $record
Check ((Read-WorkerUpgrade $data).Stage -eq 'Switched' -and (Test-Path -LiteralPath $paths.Previous)) '完整备份后切换候选，原程序保留'
if($CheckStartupInterop){
    $testAssembly=Join-Path $RepositoryRoot 'tests/CRS.Tests.Background/bin/Release/net10.0-windows/CRS.Tests.Background.dll'
    & dotnet $testAssembly --upgrade-guard-probe $data $program
    if($LASTEXITCODE -ne 0){throw 'PowerShell与C#维护日志互操作失败；需先构建当前测试项目。'}
    $record=Read-WorkerUpgrade $data
    Check ($record.Stage -eq 'Starting' -and $record.NewFactsPossible) 'PowerShell暂存、快照和日志可由真实C#启动门禁读取并标记风险'
}
$originalDb=(Get-FileHash (Join-Path $data 'scheduler-config.db')).Hash
$record.NewFactsPossible=$true;Set-WorkerUpgradeStage $data $record 'NeedsManualRepair'
Reject {Restore-WorkerPrevious $data $program $record} '可能已有新事实时拒绝旧程序和旧库回退'
Check ((Get-FileHash (Join-Path $data 'scheduler-config.db')).Hash -eq $originalDb) '拒绝危险回退后原库未改变'
$record.NewFactsPossible=$false;Set-WorkerUpgradeStage $data $record 'NeedsManualRepair'
Restore-WorkerPrevious $data $program $record
Check ((Read-WorkerUpgrade $data).Stage -eq 'RolledBack' -and (Get-FileHash (Join-Path $program 'release.json')).Hash -eq $record.PreviousReleaseHash) '无新事实时校验原代次并恢复旧程序'
Check ((Get-FileHash (Join-Path $data 'scheduler-config.db')).Hash -eq $originalDb -and (Test-Path -LiteralPath $paths.Failed)) '程序回退不覆盖数据库，失败候选保留诊断'
# 在两次目录改名之间模拟断电；日志仍为Staged，恢复不得猜测已进入候选。
$crashProgram=Join-Path $root 'crash-program';Package $crashProgram '2.0.0'
$crashRecord=$record.Clone();$crashRecord.Id=[Guid]::NewGuid().ToString('N');$crashRecord.Stage='Staged'
# 合成事务使用独立升级ID，必须同时创建对应快照而不能复用旧ID的清单哈希。
New-WorkerSnapshot $data $crashRecord
$crashPaths=Get-WorkerUpgradePaths $crashProgram $crashRecord
Copy-WorkerPackage $candidate $crashPaths.Stage $crashProgram
[IO.Directory]::Move($crashPaths.Active,$crashPaths.Previous)
Set-WorkerUpgradeStage $data $crashRecord 'Staged'
Restore-WorkerPrevious $data $crashProgram $crashRecord
Check ((Test-Path -LiteralPath $crashPaths.Active) -and (Read-WorkerUpgrade $data).Stage -eq 'RolledBack') '第一次目录改名后崩溃可恢复原程序，数据保持原地'
foreach($point in @('RollbackCandidateMoved','RollbackPreviousMoved')){
    $rollbackProgram=Join-Path $root $point;Package $rollbackProgram '2.0.0'
    $rollbackRecord=$record.Clone();$rollbackRecord.Id=[Guid]::NewGuid().ToString('N');$rollbackRecord.Stage='Switched'
    $rollbackRecord.PreviousReleaseHash=(Get-FileHash (Join-Path $rollbackProgram 'release.json')).Hash
    New-WorkerSnapshot $data $rollbackRecord
    $rollbackPaths=Get-WorkerUpgradePaths $rollbackProgram $rollbackRecord
    [IO.Directory]::Move($rollbackPaths.Active,$rollbackPaths.Previous)
    Copy-WorkerPackage $candidate $rollbackPaths.Active $rollbackPaths.Previous
    Set-WorkerUpgradeStage $data $rollbackRecord 'Switched'
    $hit=@{Value=$false}
    $interrupt={param($stage) if($stage -eq $point){$hit.Value=$true;throw 'synthetic-rollback-interruption'}}.GetNewClosure()
    Reject {Restore-WorkerPrevious $data $rollbackProgram $rollbackRecord $interrupt} "$point 故障保留恢复现场"
    Check $hit.Value "$point 确认在目标改名之后中断"
    Restore-WorkerPrevious $data $rollbackProgram (Read-WorkerUpgrade $data)
    Check ((Read-WorkerUpgrade $data).Stage -eq 'RolledBack' -and (Test-Path -LiteralPath $rollbackPaths.Failed)) "$point 重试恢复原程序且保留失败候选"
    Check ((Get-FileHash (Join-Path $data 'scheduler-config.db')).Hash -eq $originalDb) "$point 重试不覆盖数据库"
}
Write-Output "Worker维护文件事务通过：$($script:passed)项；全部使用隔离合成文件，未操作SCM。"
