# 由桌面内嵌到管理员 PowerShell 命令。压缩包内容只能在受保护目录内通过签名及文件核验后执行。
$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$stage=$null
$exitCode=2
try {
    $machine=[Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine','Registry64')
    try {
        $policy=$machine.OpenSubKey('SOFTWARE\CRS\WorkerUpdates')
        if(-not $policy){throw '受信发布者未配置。'}
        try {$publisher=$policy.GetValue('PublisherThumbprint')}finally{$policy.Dispose()}
    } finally {$machine.Dispose()}
    if($publisher -isnot [string] -or $publisher -cnotmatch '^[0-9a-fA-F]{40}$'){throw '受信发布者指纹未配置。'}
    $root=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CRS'
    $maintenance=Join-Path $root 'Background/WorkerMaintenance.psm1'
    for($current=$maintenance;$current;$current=[IO.Path]::GetDirectoryName($current)){
        if([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint){throw '安装路径含链接。'}
    }
    Import-Module -Name $maintenance -Force

    $stageName='WorkerUpdate-'+[Guid]::NewGuid().ToString('N')
    $stage=Get-WorkerChild $root $stageName
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach($sidText in @('S-1-5-18','S-1-5-32-544')){
        $sid=[Security.Principal.SecurityIdentifier]::new($sidText)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))
    }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($stage),$acl)
    $archiveStream=[IO.File]::Open($archivePath,'Open','Read','Read')
    try {
        if($archiveStream.Length -gt 256MB -or
            [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archiveStream)) -ne $expectedHash){throw '安装包哈希不符。'}
        $archiveStream.Position=0
        $zip=[IO.Compression.ZipArchive]::new($archiveStream,[IO.Compression.ZipArchiveMode]::Read,$true)
        try {
            if($zip.Entries.Count -gt 10000){throw '安装包文件数超限。'}
            $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            $buffer=[byte[]]::new(65536)
            $total=0L
            foreach($entry in $zip.Entries){
                $relative=$entry.FullName.TrimEnd('/')
                $target=Get-WorkerChild $stage $relative
                foreach($part in $relative.Replace('\','/').Split('/')){
                    if($part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
                        $part -match '^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)'){throw '安装包文件名无效。'}
                }
                if(-not $seen.Add($target)){throw '安装包含重复路径。'}
                if($entry.FullName.EndsWith('/')){continue}
                if($entry.Length -gt 1GB - $total){throw '安装包解压大小超限。'}
                $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
                $inputFile=$entry.Open()
                try {
                    $output=[IO.File]::Open($target,'CreateNew','Write','None')
                    try {
                        $written=0L
                        while(($read=$inputFile.Read($buffer,0,$buffer.Length)) -gt 0){
                            $written+=$read
                            if($written -gt $entry.Length){throw '安装包条目大小不符。'}
                            $output.Write($buffer,0,$read)
                        }
                        if($written -ne $entry.Length){throw '安装包条目不完整。'}
                        $total+=$written
                    } finally { $output.Dispose() }
                } finally { $inputFile.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $archiveStream.Dispose() }

    $candidate=Test-WorkerPackage $stage $publisher
    if($candidate.version -ne $expectedVersion -or -not $candidate.ContainsKey('desktopProtocol') -or
        $candidate.desktopProtocol -ne $expectedProtocol -or -not $candidate.ContainsKey('development') -or
        $candidate.development -isnot [bool] -or $candidate.development){throw '签名发行的版本或桌面协议不符。'}
    if(-not $candidate.files.ContainsKey('Upgrade-Service.ps1')){throw '签名发行缺少升级入口。'}
    $upgrade=Get-WorkerChild $stage 'Upgrade-Service.ps1'
    $exitCode=3
    & $upgrade -PackageDirectory $stage -TrustedPublisherThumbprint $publisher -AllowInstalledDevelopmentPackage -StartupTimeoutSeconds 420
    $exitCode=0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
} finally {
    if($stage -and [IO.Directory]::Exists($stage)){
        try {
            $checkedStage=Get-WorkerChild $root $stageName
            if($checkedStage -eq $stage){
                $null=@(Get-WorkerFiles $checkedStage)
                [IO.Directory]::Delete($checkedStage,$true)
            }
        } catch { [Console]::Error.WriteLine('无法清理安装暂存目录。') }
    }
}
exit $exitCode
