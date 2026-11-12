#Requires -Version 7.0
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$serviceName = 'CRSAiGateway'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw 'Gateway 服务已存在。请保留授权与审计文件，按维护流程处理现有服务。'
}

$package = Get-Item -LiteralPath $PackageDirectory
if (-not $package.PSIsContainer -or ($package.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw '请选择可信的 Gateway 发布目录。'
}
foreach ($name in @('CRS.AI.Gateway.exe', 'CRS.AI.Gateway.dll', 'CRS.AI.Gateway.deps.json', 'CRS.AI.Gateway.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package.FullName $name) -PathType Leaf)) {
        throw '发布目录缺少 Gateway 程序文件。'
    }
}
if (Get-ChildItem -LiteralPath $package.FullName -Recurse -Force -Attributes ReparsePoint) {
    throw 'Gateway 发布目录不能包含符号链接或目录联接。'
}

$programRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) $serviceName
$dataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) $serviceName
if ((Test-Path -LiteralPath $programRoot) -or (Test-Path -LiteralPath $dataRoot)) {
    throw '目标目录已存在，首次安装不会覆盖程序、授权或审计。'
}

$serviceSidText = [regex]::Match((& sc.exe showsid $serviceName | Out-String), 'S-1-5-80(?:-\d+){5}').Value
if ($LASTEXITCODE -ne 0 -or -not $serviceSidText) { throw '无法确定 Gateway 服务 SID。' }
$serviceSid = [Security.Principal.SecurityIdentifier]::new($serviceSidText)
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$users = [Security.Principal.SecurityIdentifier]::new('S-1-5-11')
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'

function Set-GatewayDirectoryAcl($Path, $Reader, [Security.AccessControl.FileSystemRights]$Rights) {
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($admins, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($Reader, $Rights, $inherit, 'None', 'Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Initialize-GatewayFile($Path, $Content, [Security.AccessControl.FileSystemRights]$ServiceRights) {
    [IO.File]::WriteAllText($Path, $Content)
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner($admins)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($admins, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, $ServiceRights, 'Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# 先封闭数据目录再创建文件。服务可替换授权文件，审计文件仅开放读写权限。
New-Item -ItemType Directory -Path $programRoot, $dataRoot | Out-Null
Set-GatewayDirectoryAcl $programRoot $users 'ReadAndExecute'
Set-GatewayDirectoryAcl $dataRoot $serviceSid 'ReadAndExecute'
$authorization = Join-Path $dataRoot 'Authorization'
$audit = Join-Path $dataRoot 'Audit'
New-Item -ItemType Directory -Path $authorization, $audit | Out-Null
Set-GatewayDirectoryAcl $authorization $serviceSid 'Modify'
Set-GatewayDirectoryAcl $audit $serviceSid 'ReadAndExecute'
Initialize-GatewayFile (Join-Path $authorization 'subjects.json') '[]' 'Modify'
Initialize-GatewayFile (Join-Path $audit 'events.jsonl') '' 'Read, Write'

Get-ChildItem -LiteralPath $package.FullName -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $programRoot -Recurse -Force
}
$app = Join-Path $programRoot 'CRS.AI.Gateway.exe'
$binary = '"' + $app + '" --service'
& sc.exe create $serviceName binPath= $binary start= demand obj= "NT SERVICE\$serviceName"
if ($LASTEXITCODE -ne 0) { throw 'Gateway 服务注册失败。' }
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
& $app --status
if ($LASTEXITCODE -ne 0) { throw 'Gateway 本地控制管道健康检查失败。' }
Write-Output 'Gateway 本地服务已启动，授权表为空。'
