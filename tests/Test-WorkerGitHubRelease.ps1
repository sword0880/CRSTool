#Requires -Version 7.4
$ErrorActionPreference='Stop'
$repository=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repository 'tools/background/WorkerMaintenance.psm1') -Force
$tempRoot=[IO.Path]::GetTempPath()
$name='CRS.WorkerReleaseTest-'+[Guid]::NewGuid().ToString('N')
$root=Get-WorkerChild $tempRoot $name
$source=Join-Path $root 'package'
$null=[IO.Directory]::CreateDirectory($source)
$rsa=[Security.Cryptography.RSA]::Create(2048)
try {
    Set-Content (Join-Path $source 'CRS.Worker.Service.exe') -Value 'synthetic worker binary'
    Set-Content (Join-Path $source 'trusted-background-plugins.json') -Value '[]'
    Set-Content (Join-Path $source 'Upgrade-Service.ps1') -Value 'throw "test must never invoke the updater"'
    $files=@{}
    Get-ChildItem $source -File | ForEach-Object {$files[$_.Name]=(Get-FileHash $_.FullName).Hash}
    Write-WorkerJson (Join-Path $source 'release.json') @{
        format=1;product='CRS.Worker.Service';version='2.0.1';files=$files;development=$true
        maintenanceFormat=1;upgradeFromVersions=@('2.0.0')
    }
    $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=CRS signed release fixture',$rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $usage=[Security.Cryptography.OidCollection]::new()
    $null=$usage.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage,$true))
    $cert=$request.CreateSelfSigned([DateTimeOffset]::Now.AddHours(-1),[DateTimeOffset]::Now.AddHours(1))
    try {
        $arguments=@{PackageDirectory=$source;ExpectedVersion='2.0.1';UpgradeFromVersions=@('2.0.0');Certificate=$cert;OutputDirectory=(Join-Path $root 'release')}
        $zip=& (Join-Path $repository 'tools/background/New-WorkerGitHubRelease.ps1') @arguments
        $expanded=Join-Path $root 'expanded'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip,$expanded)
        $manifest=Test-WorkerPackage $expanded -Development
        if($manifest.desktopProtocol -ne 2 -or $manifest.development -ne $false -or $manifest.version -ne '2.0.1'){
            throw '签名发行版本、协议或正式标记有误。'
        }
        $content=[Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes((Join-Path $expanded 'release.json')))
        $cms=[Security.Cryptography.Pkcs.SignedCms]::new($content,$true)
        $cms.Decode([IO.File]::ReadAllBytes((Join-Path $expanded 'release.json.p7s')))
        $cms.CheckSignature($true)
        if($cms.SignerInfos[0].Certificate.Thumbprint -ne $cert.Thumbprint){throw '签名证书不符。'}
        'PASS Worker 更新：ZIP、正式发行清单、通信协议、签名和文件索引。'
        $rejected=$false
        try {$null=Test-WorkerPackage $expanded $cert.Thumbprint}catch{$rejected=$true}
        if(-not $rejected){throw '正式维护接受了未安装在信任链的测试证书。'}
        'PASS Worker 更新：仅提供证书指纹无法让未经信任的签名通过正式验证。'

        Import-Module (Join-Path $repository 'tools/background/WorkerUpgradeTransaction.psm1') -Force
        $data=Join-Path $root 'data'
        $null=[IO.Directory]::CreateDirectory($data)
        $service=@{Disable={throw '禁止控制服务'}}
        $rejected=$false
        try {
            Invoke-WorkerUpgradeTransaction -Data $data -Program $expanded -Source $source -Service $service `
                -Publisher $cert.Thumbprint -AllowInstalledDevelopmentPackage
        } catch {$rejected=$_.Exception.Message -match 'release\.json\.p7s'}
        if(-not $rejected -or (Read-WorkerUpgrade $data)){throw '兼容已安装开发包时仍须在创建事务前核验候选签名。'}
        'PASS Worker 更新：开发版过渡开关不允许安装未签名候选。'
    } finally {$cert.Dispose()}
} finally {
    $rsa.Dispose()
    $checkedRoot=Get-WorkerChild $tempRoot $name
    if($checkedRoot -eq $root){
        $null=@(Get-WorkerFiles $checkedRoot)
        [IO.Directory]::Delete($checkedRoot,$true)
    }
}
