#Requires -Version 7.0
#Requires -RunAsAdministrator
param([string]$PackageDirectory=$PSScriptRoot,[string]$TrustedPublisherThumbprint,[switch]$AllowDevelopmentPackage)
$ErrorActionPreference='Stop'
$serviceName='CRSBackgroundService'
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw '服务已存在。此安装器拒绝覆盖升级，请先按维护流程备份并停止旧版本。'}
$source=[IO.Path]::GetFullPath($PackageDirectory)
Import-Module (Join-Path $PSScriptRoot 'WorkerMaintenance.psm1') -Force
$releasePath=Join-Path $source 'release.json'
$release=Test-WorkerPackage $source $TrustedPublisherThumbprint -Development:$AllowDevelopmentPackage
$runtimes=& dotnet --list-runtimes
if(-not ($runtimes -match '^Microsoft.NETCore.App 10\.')){throw '请先安装 .NET 10 x64 运行时。'}
$target=Join-Path $env:ProgramFiles 'CRS/Background'
$data=Join-Path $env:ProgramData 'CRS/Background'
$maintenance=Read-WorkerUpgrade $data
if($maintenance){
    $expectedRelease=if($maintenance.Stage -eq 'RolledBack'){$maintenance.PreviousReleaseHash}else{$maintenance.CandidateReleaseHash}
    if($maintenance.Stage -notin @('Committed','RolledBack') -or (Get-FileHash -LiteralPath $releasePath).Hash -ne $expectedRelease){throw '已有Worker维护日志，只允许相同已确认发行重装；版本变化必须走升级/恢复入口。'}
}
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
if(Test-Path -LiteralPath (Join-Path $source 'release.json.p7s')){Copy-Item -LiteralPath (Join-Path $source 'release.json.p7s') -Destination $target}
if(-not [Diagnostics.EventLog]::SourceExists($serviceName)){New-EventLog -LogName Application -Source $serviceName}
$binary='"'+(Join-Path $target 'CRS.Worker.Service.exe')+'"'
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
$deadline=[DateTime]::UtcNow.AddSeconds(30)
do{
    & (Join-Path $target 'CRS.Worker.Service.exe') --health-probe
    $ready=$LASTEXITCODE -eq 0
    if(-not $ready){Start-Sleep -Milliseconds 250}
}while(-not $ready -and [DateTime]::UtcNow -lt $deadline)
if(-not $ready){throw 'SCM已注册，但应用健康未就绪；保留程序和数据用于诊断，禁止当作安装成功。'}
Write-Output 'CRS.Worker.Service 已独立安装，插件与加密库健康已确认。'
