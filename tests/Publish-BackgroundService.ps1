param([Parameter(Mandatory)][string]$OutputDirectory,[ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw '发布目录必须不存在，以保持发行包不可变。'}
New-Item -ItemType Directory -Path $output | Out-Null
# 框架依赖、非裁剪发行；后台包不能携带 WPF 运行时。
& dotnet publish (Join-Path $repository 'src/CRS.Worker.Service') -c $Configuration --no-self-contained -o $output -p:NuGetAudit=false --no-restore -m:1
if($LASTEXITCODE -ne 0){throw '服务发布失败。'}
$package=Join-Path $output 'Plugins/crs.heartbeat/2.0.0'
& dotnet publish (Join-Path $repository 'src/Plugins/CRS.Plugins.Heartbeat') -c $Configuration --no-self-contained -o $package -p:NuGetAudit=false --no-restore -m:1
if($LASTEXITCODE -ne 0){throw '心跳插件发布失败。'}
$manifest=@{id='crs.heartbeat';version='2.0.0';hostContract='2.0';backgroundEntry=@{assembly='CRS.Plugins.Heartbeat.dll';type='CRS.Plugins.Heartbeat.HeartbeatEntry'};capabilities=@('public-data-jobs')}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $package 'plugin.json') -Encoding utf8
$hashes=@{}
foreach($file in Get-ChildItem -LiteralPath $package -Recurse -File){$relative=[IO.Path]::GetRelativePath($package,$file.FullName).Replace('\','/');$hashes[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
@(@{id='crs.heartbeat';version='2.0.0';directory='crs.heartbeat/2.0.0';files=$hashes}) | ConvertTo-Json -Depth 8 -AsArray | Set-Content (Join-Path $output 'trusted-background-plugins.json') -Encoding utf8
# 安装脚本随发行提供；完整索引需在正式发行时由组织证书生成 release.json.p7s。
Copy-Item -LiteralPath (Join-Path $repository 'tools/background/Install-Service.ps1'),(Join-Path $repository 'tools/background/Uninstall-Service.ps1') -Destination $output
$files=@{}
foreach($file in Get-ChildItem -LiteralPath $output -Recurse -File){$relative=[IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/');$files[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
@{format=1;product='CRS.Worker.Service';version='2.0.0';development=$true;files=$files} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'release.json') -Encoding utf8
Write-Output "已生成独立服务开发发行包：$output（尚未签署生产发行签名）。"
