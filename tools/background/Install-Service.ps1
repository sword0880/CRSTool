#Requires -Version 7.0
#Requires -RunAsAdministrator
param([string]$PackageDirectory=$PSScriptRoot,[string]$TrustedPublisherThumbprint,[switch]$AllowDevelopmentPackage)
$ErrorActionPreference='Stop'
$serviceName='CRSBackgroundService'
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw '服务已存在。此安装器拒绝覆盖升级，请先按维护流程备份并停止旧版本。'}
$source=[IO.Path]::GetFullPath($PackageDirectory)
if((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '发行目录不能是重解析点。'}
$releasePath=Join-Path $source 'release.json'
$release=Get-Content -Raw -LiteralPath $releasePath | ConvertFrom-Json -AsHashtable
if($release.format -ne 1 -or $release.product -ne 'CRS.Background.Service'){throw '发行格式不兼容。'}
if(-not $AllowDevelopmentPackage){
    # 生产发行以外部配置的证书指纹验证分离 CMS 签名；不能让包自报受信发布者。
    if(-not $TrustedPublisherThumbprint){throw '正式安装必须指定受信发布者证书指纹；开发验收请显式使用 -AllowDevelopmentPackage。'}
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $content=[Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($releasePath))
    $cms=[Security.Cryptography.Pkcs.SignedCms]::new($content,$true)
    $cms.Decode([IO.File]::ReadAllBytes((Join-Path $source 'release.json.p7s')))
    $cms.CheckSignature($false)
    if($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $TrustedPublisherThumbprint){throw '发行签名发布者不受信。'}
}
foreach($relative in $release.files.Keys){
    $file=[IO.Path]::GetFullPath((Join-Path $source $relative))
    if([IO.Path]::IsPathRooted($relative) -or -not $file.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '发行文件路径越界。'}
    $current=$file
    while($current -and $current.StartsWith($source,[StringComparison]::OrdinalIgnoreCase)){
        if((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '发行文件不可包含链接。'}
        $current=Split-Path $current -Parent
    }
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $release.files[$relative]){throw "发行完整性失败：$relative"}
}
foreach($file in Get-ChildItem -LiteralPath $source -File -Recurse){
    $relative=[IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','/')
    if($relative -notin @('release.json','release.json.p7s') -and -not $release.files.ContainsKey($relative)){throw "发行包含未登记文件：$relative"}
}
$runtimes=& dotnet --list-runtimes
if(-not ($runtimes -match '^Microsoft.NETCore.App 10\.')){throw '请先安装 .NET 10 x64 运行时。'}
$target=Join-Path $env:ProgramFiles 'CRS/Background'
$data=Join-Path $env:ProgramData 'CRS/Background'
if(Test-Path -LiteralPath $target){throw '程序目标目录已存在，拒绝混合不同发行文件。'}
# 数据目录可以来自保留数据的卸载；旧密钥不会被安装器更换。
foreach($path in @((Join-Path $env:ProgramFiles 'CRS'),(Join-Path $env:ProgramData 'CRS'),$data)){
    if((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '安装目标不能包含链接。'}
}
New-Item -ItemType Directory -Path $target,$data -Force | Out-Null
# 先封闭目录 ACL，再复制二进制；普通桌面用户无服务数据读写权限。
function Set-PrivateDirectory([string]$Path,[Security.Principal.SecurityIdentifier]$ServiceSid,[bool]$Program){
    $acl=[Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    foreach($sidText in @('S-1-5-18','S-1-5-32-544')){
        $sid=[Security.Principal.SecurityIdentifier]::new($sidText)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))
    }
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    $rights=if($Program){'ReadAndExecute'}else{'Modify'}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($ServiceSid,$rights,$inherit,'None','Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
$localService=[Security.Principal.SecurityIdentifier]::new('S-1-5-19')
Set-PrivateDirectory $target $localService $true
Set-PrivateDirectory $data $localService $false
foreach($relative in $release.files.Keys){
    $destination=Join-Path $target $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $destination
    if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $release.files[$relative]){throw '复制后发行验证失败。'}
}
Copy-Item -LiteralPath $releasePath -Destination $target
if(-not [Diagnostics.EventLog]::SourceExists($serviceName)){New-EventLog -LogName Application -Source $serviceName}
$binary='"'+(Join-Path $target 'CRS.Background.Service.exe')+'"'
New-Service -Name $serviceName -DisplayName 'CRS Background Service' -BinaryPathName $binary -StartupType Automatic -Credential ([PSCredential]::new('NT AUTHORITY\LocalService',[Security.SecureString]::new())) | Out-Null
function Invoke-ServiceConfig([string[]]$Arguments){& sc.exe @Arguments;if($LASTEXITCODE -ne 0){throw 'SCM 配置失败，请保留现场诊断。'}}
Invoke-ServiceConfig @('config',$serviceName,'start=','delayed-auto')
Invoke-ServiceConfig @('sidtype',$serviceName,'unrestricted')
Invoke-ServiceConfig @('failure',$serviceName,'reset=','86400','actions=','restart/60000/restart/300000/none/0')
Invoke-ServiceConfig @('failureflag',$serviceName,'1')
# 服务 SID 比共享 LocalService 身份更精确；程序读权限仍允许 LocalService，数据仅授予此服务 SID。
$serviceSid=([Security.Principal.NTAccount]::new('NT SERVICE',$serviceName)).Translate([Security.Principal.SecurityIdentifier])
Set-PrivateDirectory $data $serviceSid $false
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
Write-Output 'CRS.Background.Service 已独立安装并启动。请通过后台服务页面确认任务与历史健康状态。'
