#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string[]]$UpgradeFromVersions,
    [Parameter(Mandatory)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,100)][int]$DesktopProtocol=2
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'WorkerMaintenance.psm1') -Force
$source=[IO.Path]::GetFullPath($PackageDirectory)
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory)
Assert-WorkerNoLinks $outputRoot
if($outputRoot -eq $source -or $outputRoot.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
    throw '输出目录必须位于发布包之外。'
}
$release=Test-WorkerPackage $source -Development
if($ExpectedVersion -cnotmatch '^\d+\.\d+\.\d+$' -or $ExpectedVersion -ne $release.version -or
    @($UpgradeFromVersions | Where-Object { $_ -cnotmatch '^\d+\.\d+\.\d+$' -or [version]$_ -ge [version]$ExpectedVersion }).Count){
    throw '正式更新须与二进制发行版本一致，并从更早的版本升级。'
}
$codeSigning=@($Certificate.Extensions |
    Where-Object {$_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]} |
    ForEach-Object {$_.EnhancedKeyUsages} | Where-Object {$_.Value -eq '1.3.6.1.5.5.7.3.3'})
if(-not $Certificate.HasPrivateKey -or [DateTime]::Now -lt $Certificate.NotBefore -or [DateTime]::Now -gt $Certificate.NotAfter -or
    $codeSigning.Count -eq 0){
    throw '需要带私钥且有效的代码签名证书。'
}
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$null=[IO.Directory]::CreateDirectory($outputRoot)
$zip=Get-WorkerChild $outputRoot 'CRS.Worker.Service-win-x64.zip'
if(Test-Path -LiteralPath $zip){throw '正式安装包输出已存在。'}
$stage=Get-WorkerChild $outputRoot ('signing-'+[Guid]::NewGuid().ToString('N'))
$tempZip=$stage+'.zip'
try {
    Copy-Item -LiteralPath $source -Destination $stage -Recurse
    $release.development=$false
    $release.desktopProtocol=$DesktopProtocol
    $release.upgradeFromVersions=@($UpgradeFromVersions | Sort-Object -Unique)
    $manifest=Get-WorkerChild $stage 'release.json'
    Write-WorkerJson $manifest $release
    $content=[Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes($manifest))
    $cms=[Security.Cryptography.Pkcs.SignedCms]::new($content,$true)
    $signer=[Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.IncludeOption=[Security.Cryptography.X509Certificates.X509IncludeOption]::ExcludeRoot
    $cms.ComputeSignature($signer,$true)
    $cms.CheckSignature($true)
    [IO.File]::WriteAllBytes((Get-WorkerChild $stage 'release.json.p7s'),$cms.Encode())
    $null=Test-WorkerPackage $stage -Development
    [IO.Compression.ZipFile]::CreateFromDirectory($stage,$tempZip,[IO.Compression.CompressionLevel]::Optimal,$false)
    if((Get-Item -LiteralPath $tempZip).Length -gt 256MB){throw '正式安装包超出桌面的下载上限。'}
    [IO.File]::Move($tempZip,$zip)
    Write-Output $zip
} finally {
    [IO.File]::Delete($tempZip)
    if([IO.Directory]::Exists($stage)){
        $checkedStage=Get-WorkerChild $outputRoot ([IO.Path]::GetFileName($stage))
        if($checkedStage -eq $stage){
            $null=@(Get-WorkerFiles $checkedStage)
            [IO.Directory]::Delete($checkedStage,$true)
        }
    }
}
