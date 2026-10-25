Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-WorkerNoLinks([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        # 每个文件仍逐级重新检查；直接读取属性，避免数千文件反复构造PowerShell提供程序对象。
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw '维护路径不可包含链接。' }
    }
}
function Get-WorkerChild([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or @($Relative.Replace('\','/').Split('/') | Where-Object { $_ -in @('','..','.') }).Count) { throw '维护相对路径无效。' }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '维护路径越界。' }
    Assert-WorkerNoLinks $path
    return $path
}
function Get-WorkerFiles([string]$Root,[switch]$ExcludeMaintenance) {
    Assert-WorkerNoLinks $Root
    $base=[IO.Path]::GetFullPath($Root)
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($base)
    $count = 0
    while ($pending.Count) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '维护文件数量超限或包含链接。' }
            # 旧维护归档本就不属于本次业务快照；必须在遍历前排除，不能先扫描十万旧文件再过滤。
            # 仍检查归档根是否为链接；实际业务树、每份快照及程序包仍保留原十万条目上限与逐路径检查。
            if($ExcludeMaintenance -and $entry.PSIsContainer -and $entry.FullName.Equals((Join-Path $base 'maintenance'),[StringComparison]::OrdinalIgnoreCase)){continue}
            if (++$count -gt 100000) { throw '维护文件数量超限或包含链接。' }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) } else { $entry }
        }
    }
}
function Write-WorkerJson([string]$Path, $Value) {
    Assert-WorkerNoLinks $Path
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 12))
    $output = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $output.Write($bytes); $output.Flush($true) } finally { $output.Dispose() }
    [IO.File]::Move($temporary, $Path, $true)
}
function Read-WorkerUpgrade([string]$Data) {
    $path = Get-WorkerChild $Data 'maintenance/upgrade.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $inputStream = [IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if ($inputStream.Length -gt 16384) { throw '维护日志超限。' }
        $reader = [IO.StreamReader]::new($inputStream)
        try { $record = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable } finally { $reader.Dispose() }
    } finally { $inputStream.Dispose() }
    $stages = @('Prepared','BackedUp','Staged','Switched','Starting','Healthy','Committed','RollingBack','RolledBack','NeedsManualRepair')
    $parsedId = [Guid]::Empty; $parsedVersion = $null
    if ($record.Format -ne 1 -or -not [Guid]::TryParseExact($record.Id,'N',[ref]$parsedId) -or $record.Stage -notin $stages -or
        $record.PreviousReleaseHash -notmatch '^[a-fA-F0-9]{64}$' -or $record.CandidateReleaseHash -notmatch '^[a-fA-F0-9]{64}$' -or
        -not [Version]::TryParse($record.PreviousVersion,[ref]$parsedVersion) -or -not [Version]::TryParse($record.CandidateVersion,[ref]$parsedVersion) -or
        $record.NewFactsPossible -isnot [bool] -or ($record.Stage -in @('Starting','Healthy','Committed') -and -not $record.NewFactsPossible) -or
        ($record.Stage -in @('Switched','Starting','Healthy','Committed') -and -not $record.BackupManifestHash) -or
        ($record.Stage -eq 'RolledBack' -and $record.NewFactsPossible) -or
        ($record.BackupManifestHash -and $record.BackupManifestHash -notmatch '^[a-fA-F0-9]{64}$')) { throw '维护日志格式或状态无效。' }
    return $record
}
function Set-WorkerUpgradeStage([string]$Data, [hashtable]$Record, [string]$Stage) {
    $Record.Stage = $Stage; $Record.UpdatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Write-WorkerJson (Get-WorkerChild $Data 'maintenance/upgrade.json') $Record
}
function Test-WorkerPackage([string]$Directory, [string]$Publisher, [switch]$Development) {
    $root = [IO.Path]::GetFullPath($Directory)
    Assert-WorkerNoLinks $root
    $releasePath = Get-WorkerChild $root 'release.json'
    if ((Get-Item -LiteralPath $releasePath).Length -gt 2MB) { throw '发行清单超限。' }
    $release = Get-Content -Raw -LiteralPath $releasePath | ConvertFrom-Json -AsHashtable
    $version = $null
    if ($release.format -ne 1 -or $release.product -ne 'CRS.Worker.Service' -or
        -not [Version]::TryParse($release.version,[ref]$version) -or $release.files -isnot [Collections.IDictionary] -or
        $release.files.Count -lt 1 -or $release.files.Count -gt 10000) { throw '发行格式不兼容。' }
    if (-not $Development) {
        if (-not $Publisher) { throw '正式维护必须指定外部受信发布者指纹。' }
        Add-Type -AssemblyName System.Security.Cryptography.Pkcs
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($releasePath)), $true)
        $cms.Decode([IO.File]::ReadAllBytes((Get-WorkerChild $root 'release.json.p7s')))
        $cms.CheckSignature($false)
        if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $Publisher) { throw '发行签名发布者不受信。' }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in $release.files.Keys) {
        if ($relative -in @('release.json','release.json.p7s') -or $release.files[$relative] -notmatch '^[a-fA-F0-9]{64}$') { throw '发行文件索引无效。' }
        $file = Get-WorkerChild $root $relative
        if (-not $seen.Add($file)) { throw '发行包含重复目标路径。' }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $release.files[$relative]) { throw '发行文件哈希不一致。' }
    }
    foreach ($file in Get-WorkerFiles $root) {
        $relative = [IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        if ($relative -notin @('release.json','release.json.p7s') -and -not $release.files.ContainsKey($relative)) { throw '发行包含未登记文件。' }
    }
    if (-not $release.files.ContainsKey('CRS.Worker.Service.exe') -or -not $release.files.ContainsKey('trusted-background-plugins.json')) { throw '发行缺少Worker入口或插件索引。' }
    return $release
}
function Copy-WorkerPackage([string]$Source, [string]$Target, [string]$AclSource) {
    Assert-WorkerNoLinks $Target
    if (Test-Path -LiteralPath $Target) { throw '候选程序目录必须不存在。' }
    New-Item -ItemType Directory -Path $Target | Out-Null
    # 先采用既有受限程序ACL，再写文件；不能依赖ProgramFiles父目录默认继承。
    Set-Acl -LiteralPath $Target -AclObject (Get-Acl -LiteralPath $AclSource)
    foreach ($file in Get-WorkerFiles $Source) {
        $destination = Get-WorkerChild $Target ([IO.Path]::GetRelativePath($Source,$file.FullName))
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
function New-WorkerSnapshot([string]$Data, [hashtable]$Record) {
    $destination = Get-WorkerChild $Data ('maintenance/' + $Record.Id + '/snapshot')
    if (Test-Path -LiteralPath $destination) { throw '升级快照已存在，拒绝覆盖。' }
    $files = @(Get-WorkerFiles $Data -ExcludeMaintenance | Where-Object {
        $relative = [IO.Path]::GetRelativePath($Data,$_.FullName).Replace('\','/')
        $relative -notin @('owner.lock','maintenance.lock') -and -not $relative.StartsWith('maintenance/',[StringComparison]::OrdinalIgnoreCase)
    })
    if (-not ($files.Name -contains 'service-key.dpapi') -or -not ($files.Name -contains 'scheduler-config.db')) { throw '后台密钥或调度库缺失。' }
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($destination))
    if ($drive.AvailableFreeSpace -lt $size + 64MB) { throw '一致性备份所需磁盘空间不足。' }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $hashes = @{}
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($Data,$file.FullName).Replace('\','/')
        $target = Get-WorkerChild $destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        $before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $file.FullName -Destination $target
        if ($before -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -or
            $before -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw '快照复制期间源文件改变或副本损坏。' }
        $hashes[$relative] = $before
    }
    $manifest = Get-WorkerChild $Data ('maintenance/' + $Record.Id + '/snapshot.json')
    Write-WorkerJson $manifest @{ Format=1; ReleaseHash=$Record.PreviousReleaseHash; Files=$hashes }
    $Record.BackupManifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
    Test-WorkerSnapshot $Data $Record
}
function Test-WorkerSnapshot([string]$Data, [hashtable]$Record) {
    $root = Get-WorkerChild $Data ('maintenance/' + $Record.Id)
    $path = Get-WorkerChild $root 'snapshot.json'
    if ((Get-Item -LiteralPath $path).Length -gt 32MB -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Record.BackupManifestHash) { throw '快照清单哈希不一致。' }
    $manifest = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable
    if ($manifest.Format -ne 1 -or $manifest.ReleaseHash -ne $Record.PreviousReleaseHash -or
        $manifest.Files.Count -gt 100000 -or -not $manifest.Files.ContainsKey('service-key.dpapi') -or -not $manifest.Files.ContainsKey('scheduler-config.db')) { throw '快照清单无效。' }
    $snapshot = Get-WorkerChild $root 'snapshot'
    foreach ($relative in $manifest.Files.Keys) {
        if ((Get-FileHash -LiteralPath (Get-WorkerChild $snapshot $relative) -Algorithm SHA256).Hash -ne $manifest.Files[$relative]) { throw '快照文件缺失或哈希不一致。' }
    }
    if (@(Get-WorkerFiles $snapshot).Count -ne $manifest.Files.Count) { throw '快照包含未登记文件。' }
}
# 仅当持久快照存在时才允许证明“候选未写入新事实”。升级记录中的
# NewFactsPossible=false 只是程序状态，不能替代当前文件与停机快照的一致性。
function Assert-WorkerLiveDataMatchesSnapshot([string]$Data, [hashtable]$Record) {
    if (-not $Record.BackupManifestHash) { throw '缺少可核验快照，拒绝以文件一致性证明安全回退。' }
    Test-WorkerSnapshot $Data $Record
    $manifestPath = Get-WorkerChild $Data ('maintenance/' + $Record.Id + '/snapshot.json')
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json -AsHashtable
    # 与 New-WorkerSnapshot 保持完全相同的排除项；否则会把本次维护日志当作业务新事实。
    $actual = @(Get-WorkerFiles $Data -ExcludeMaintenance | Where-Object {
        $relative = [IO.Path]::GetRelativePath($Data, $_.FullName).Replace('\','/')
        $relative -notin @('owner.lock','maintenance.lock') -and
            -not $relative.StartsWith('maintenance/',[StringComparison]::OrdinalIgnoreCase)
    })
    if ($actual.Count -ne $manifest.Files.Count) { throw '活动数据文件数量与升级快照不一致，拒绝回退。' }
    foreach ($item in $actual) {
        $relative = [IO.Path]::GetRelativePath($Data,$item.FullName).Replace('\','/')
        if (-not $manifest.Files.ContainsKey($relative) -or
            (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ne $manifest.Files[$relative]) {
            throw '活动数据与升级快照不一致，拒绝回退。'
        }
    }
}
function Get-WorkerUpgradePaths([string]$Program, [hashtable]$Record) {
    # 所有移动只使用验证过的固定同级目录；日志不能指定任意源/目标路径。
    $id = [Guid]::ParseExact($Record.Id,'N').ToString('N')
    $parent = Split-Path ([IO.Path]::GetFullPath($Program)) -Parent
    $leaf = Split-Path $Program -Leaf
    return @{ Active=(Get-WorkerChild $parent $leaf); Stage=(Get-WorkerChild $parent ($leaf+'.stage-'+$id));
        Previous=(Get-WorkerChild $parent ($leaf+'.previous-'+$id)); Failed=(Get-WorkerChild $parent ($leaf+'.failed-'+$id)) }
}
function Switch-WorkerPackage([string]$Data, [string]$Program, [hashtable]$Record, [scriptblock]$Checkpoint=$null) {
    $paths = Get-WorkerUpgradePaths $Program $Record
    if ($Record.Stage -ne 'Staged') { throw '候选尚未完成备份与暂存。' }
    Test-WorkerSnapshot $Data $Record
    if ((Get-FileHash -LiteralPath (Get-WorkerChild $paths.Active 'release.json')).Hash -ne $Record.PreviousReleaseHash -or
        (Get-FileHash -LiteralPath (Get-WorkerChild $paths.Stage 'release.json')).Hash -ne $Record.CandidateReleaseHash -or
        (Test-Path -LiteralPath $paths.Previous)) { throw '切换前程序代次不一致。' }
    [IO.Directory]::Move($paths.Active,$paths.Previous)
    if($Checkpoint){$null=& $Checkpoint 'PreviousMoved'}
    [IO.Directory]::Move($paths.Stage,$paths.Active)
    if($Checkpoint){$null=& $Checkpoint 'CandidateMoved'}
    Set-WorkerUpgradeStage $Data $Record 'Switched'
}
function Restore-WorkerPrevious([string]$Data, [string]$Program, [hashtable]$Record, [scriptblock]$Checkpoint=$null) {
    if ($Record.NewFactsPossible) { throw '候选可能已写入新事实，禁止回退或覆盖旧数据库。' }
    # 备份完成后的回退必须确认当前原件仍与停机快照一致；
    # 文件遭删除、追加或篡改时保留现场，不切换代码代次。
    if ($Record.BackupManifestHash) { Assert-WorkerLiveDataMatchesSnapshot $Data $Record }
    $paths = Get-WorkerUpgradePaths $Program $Record
    # 回退改名中断后Failed目录可能已存在；核对指纹后继续，不覆盖或删除任何代次。
    if(Test-Path -LiteralPath $paths.Failed){
        if((Get-FileHash -LiteralPath (Get-WorkerChild $paths.Failed 'release.json')).Hash -ne $Record.CandidateReleaseHash){throw '失败候选代次不一致，保留现场。'}
    }
    if (Test-Path -LiteralPath $paths.Previous) {
        if ((Get-FileHash -LiteralPath (Get-WorkerChild $paths.Previous 'release.json')).Hash -ne $Record.PreviousReleaseHash) { throw '旧程序代次不一致。' }
        if(Test-Path -LiteralPath $paths.Active){
            if((Get-FileHash -LiteralPath (Get-WorkerChild $paths.Active 'release.json')).Hash -ne $Record.CandidateReleaseHash -or
                (Test-Path -LiteralPath $paths.Failed)){throw '活动代次不一致或候选位置冲突，保留现场。'}
        }
        Set-WorkerUpgradeStage $Data $Record 'RollingBack'
        if (Test-Path -LiteralPath $paths.Active) {
            [IO.Directory]::Move($paths.Active,$paths.Failed)
            if($Checkpoint){$null=& $Checkpoint 'RollbackCandidateMoved'}
        }
        [IO.Directory]::Move($paths.Previous,$paths.Active)
        if($Checkpoint){$null=& $Checkpoint 'RollbackPreviousMoved'}
    }
    if ((Get-FileHash -LiteralPath (Get-WorkerChild $paths.Active 'release.json')).Hash -ne $Record.PreviousReleaseHash) { throw '恢复后的程序不是原代次。' }
    # 只恢复程序，不复制旧库：停止服务后的数据从未被候选打开；不覆盖任何现有事实。
    Set-WorkerUpgradeStage $Data $Record 'RolledBack'
}
Export-ModuleMember -Function *-Worker*
