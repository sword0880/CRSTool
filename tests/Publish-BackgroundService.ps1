param([Parameter(Mandatory)][string]$OutputDirectory,[ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$HeartbeatOnly,[switch]$MigrateNationalTeamStorage,[string]$MihomoArchive)
$ErrorActionPreference='Stop'
if($HeartbeatOnly -and $MigrateNationalTeamStorage){throw '国家队存储迁移发行必须包含国家队插件。'}
$repository=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw '发布目录必须不存在，以保持发行包不可变。'}
New-Item -ItemType Directory -Path $output | Out-Null
# 框架依赖、非裁剪发行；后台包不能携带 WPF 运行时。
& dotnet publish (Join-Path $repository 'src/Worker/CRS.Worker.Service') -c $Configuration --no-self-contained -o $output -p:NuGetAudit=false --no-restore -m:1
if($LASTEXITCODE -ne 0){throw '服务发布失败。'}
if(-not(Test-Path -LiteralPath (Join-Path $output 'Runner/CRS.Worker.PluginRunner.exe'))){throw '发行包缺少插件监督进程。'}
if($MihomoArchive){
    $archive = Get-Item -LiteralPath $MihomoArchive
    if($archive.PSIsContainer -or (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash -ne 'CD6738D4648CC1A5F48049A1343B697AB510889D648F045C5C780FC1AB443A79'){
        throw '此版本需要官方 mihomo-windows-amd64-compatible-v1.19.34.zip，归档校验失败。'
    }
    $engine = Join-Path $output 'Mihomo'
    New-Item -ItemType Directory -Path $engine | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $entry = $zip.GetEntry('mihomo-windows-amd64-compatible.exe')
        if($null -eq $entry){throw '官方 Mihomo 归档缺少 Windows x64 核心。'}
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $engine 'mihomo.exe'))
    }
    finally { $zip.Dispose() }
    @'
Mihomo v1.19.34 by MetaCubeX contributors, GPL-3.0.
Source: https://github.com/MetaCubeX/mihomo/tree/v1.19.34
License: https://github.com/MetaCubeX/mihomo/blob/v1.19.34/LICENSE
Release: https://github.com/MetaCubeX/mihomo/releases/tag/v1.19.34
'@ | Set-Content -LiteralPath (Join-Path $engine 'Mihomo-NOTICE.txt') -Encoding utf8
}
$package=Join-Path $output 'Plugins/crs.heartbeat/2.0.0'
& dotnet publish (Join-Path $repository 'src/Plugins/CRS.Plugins.Heartbeat') -c $Configuration --no-self-contained -o $package -p:NuGetAudit=false --no-restore -m:1
if($LASTEXITCODE -ne 0){throw '心跳插件发布失败。'}
$manifest=@{id='crs.heartbeat';version='2.0.0';hostContract='2.0';backgroundEntry=@{assembly='CRS.Plugins.Heartbeat.dll';type='CRS.Plugins.Heartbeat.HeartbeatEntry'};capabilities=@('public-data-jobs')}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $package 'plugin.json') -Encoding utf8
$hashes=@{}
foreach($file in Get-ChildItem -LiteralPath $package -Recurse -File){$relative=[IO.Path]::GetRelativePath($package,$file.FullName).Replace('\','/');$hashes[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
@(@{id='crs.heartbeat';version='2.0.0';directory='crs.heartbeat/2.0.0';files=$hashes}) | ConvertTo-Json -Depth 8 -AsArray | Set-Content (Join-Path $output 'trusted-background-plugins.json') -Encoding utf8
# 只在宿主可靠性隔离套件中显式选择心跳包，实际安装默认包含ETF公开采集入口。
if(-not $HeartbeatOnly){
    $etf=Join-Path $output 'Plugins/crs.national-team/2.0.0'
    & (Join-Path $PSScriptRoot 'Publish-NationalTeamPackage.ps1') -OutputDirectory $etf -Configuration $Configuration
    $etfHashes=@{}
    foreach($file in Get-ChildItem -LiteralPath $etf -Recurse -File){$relative=[IO.Path]::GetRelativePath($etf,$file.FullName).Replace('\','/');$etfHashes[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
    $index=@(Get-Content (Join-Path $output 'trusted-background-plugins.json') -Raw | ConvertFrom-Json -AsHashtable)
    $index+=@{id='crs.national-team';version='2.0.0';directory='crs.national-team/2.0.0';files=$etfHashes}
    $xueqiu=Join-Path $output 'Plugins/crs.xueqiu-watch/1.0.0'
    & (Join-Path $PSScriptRoot 'Publish-XueqiuWatchPackage.ps1') -OutputDirectory $xueqiu -Configuration $Configuration -BackgroundOnly
    $xueqiuHashes=@{}
    foreach($file in Get-ChildItem -LiteralPath $xueqiu -Recurse -File){
        $relative=[IO.Path]::GetRelativePath($xueqiu,$file.FullName).Replace('\','/')
        $xueqiuHashes[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $index+=@{id='crs.xueqiu-watch';version='1.0.0';directory='crs.xueqiu-watch/1.0.0';files=$xueqiuHashes}
    ConvertTo-Json -InputObject $index -Depth 9 | Set-Content (Join-Path $output 'trusted-background-plugins.json') -Encoding utf8
}
# 安装脚本随发行提供；完整索引需在正式发行时由组织证书生成 release.json.p7s。
Copy-Item -LiteralPath (Join-Path $repository 'tools/background/Install-Service.ps1'),(Join-Path $repository 'tools/background/Uninstall-Service.ps1'),(Join-Path $repository 'tools/background/Upgrade-Service.ps1'),(Join-Path $repository 'tools/background/Recover-Service.ps1'),(Join-Path $repository 'tools/background/WorkerMaintenance.psm1'),(Join-Path $repository 'tools/background/WorkerUpgradeTransaction.psm1') -Destination $output
$files=@{}
foreach($file in Get-ChildItem -LiteralPath $output -Recurse -File){$relative=[IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/');$files[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
$workerVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output 'CRS.Worker.Service.exe'))
$version="$($workerVersion.FileMajorPart).$($workerVersion.FileMinorPart).$($workerVersion.FileBuildPart)"
$release=@{format=1;product='CRS.Worker.Service';version=$version;development=$true;maintenanceFormat=1;upgradeFromVersions=@('2.0.0',$version | Select-Object -Unique);automaticCleanupProtocol=1;dataCleanupTaskProtocol=1;maintenancePlanProtocol=1;diagnosticJobsProtocol=1;files=$files}
# 迁移只属于显式维护发行；日常构建、首次安装及普通重启不自动改写旧资料格式。
if($MigrateNationalTeamStorage){$release.storageMigrations=@(@{pluginId='crs.national-team';migrationId='national-team-plaintext-v1';timeoutSeconds=600})}
$release | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'release.json') -Encoding utf8
Write-Output "已生成独立服务开发发行包：$output（尚未签署生产发行签名）。"
