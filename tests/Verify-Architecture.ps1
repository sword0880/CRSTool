param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# 用编译项目和源码验证依赖方向；只检查业务源码，忽略构建缓存。
function Assert-Boundary([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
$domain = Join-Path $RepositoryRoot 'src/CRS.Domain'
$app = Join-Path $RepositoryRoot 'src/CRS.Application'
$infra = Join-Path $RepositoryRoot 'src/CRS.Infrastructure'
$wpf = Join-Path $RepositoryRoot 'src/CRS.Desktop.Wpf'
$security = Join-Path $RepositoryRoot 'src/CRS.Security'
[xml]$securityProject=Get-Content -Raw (Join-Path $security 'CRS.Security.csproj')
Assert-Boundary ($securityProject.SelectNodes('//ProjectReference').Count -eq 0) 'Security 不得引用其他生产项目。'
foreach($file in Get-ChildItem $security -Filter '*.cs' -Recurse | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]') {
    $source=Get-Content -Raw $file.FullName
    Assert-Boundary (-not ($source -match 'CRS\.(Domain|Application|Infrastructure|Desktop)|Microsoft\.Data\.Sqlite|Dapper|System\.IO|\b(File|Directory|FileStream)\b')) "Security 出现业务、持久化或文件依赖：$($file.Name)"
}
[xml]$domainProject = Get-Content -Raw (Join-Path $domain 'CRS.Domain.csproj')
Assert-Boundary ($domainProject.SelectNodes('//ProjectReference | //PackageReference').Count -eq 0) 'Domain 不得引用外层或 UI 包。'
[xml]$appProject = Get-Content -Raw (Join-Path $app 'CRS.Application.csproj')
$appReferences = @($appProject.SelectNodes('//ProjectReference'))
Assert-Boundary ($appReferences.Count -eq 1 -and $appReferences[0].Include -eq '../CRS.Domain/CRS.Domain.csproj') 'Application 只能引用 Domain。'
[xml]$infraProject = Get-Content -Raw (Join-Path $infra 'CRS.Infrastructure.csproj')
foreach ($reference in $infraProject.SelectNodes('//ProjectReference')) {
    Assert-Boundary ($reference.Include -match 'CRS\.(Domain|Application|Security)/CRS\.(Domain|Application|Security)\.csproj$') 'Infrastructure 反向引用前台。'
}
foreach ($root in @($domain,$app)) {
    foreach ($file in Get-ChildItem $root -Filter '*.cs' -Recurse | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]') {
        $source = Get-Content -Raw -LiteralPath $file.FullName
        Assert-Boundary (-not ($source -match '(?m)^\s*using\s+(?:CRS\.Infrastructure|CRS\.Desktop|NPOI|Dapper|NLog|Wpf\.Ui|LiveChartsCore|System\.Windows)\b')) "业务层反向依赖实现：$($file.Name)"
    }
}
foreach ($entry in @(@($wpf,'Views'),@($wpf,'ViewModels'),@($wpf,'Controls'))) {
    foreach ($file in Get-ChildItem (Join-Path $entry[0] $entry[1]) -Filter '*.cs' -Recurse) {
        $source = Get-Content -Raw -LiteralPath $file.FullName
        Assert-Boundary (-not ($source -match '(?m)^\s*using\s+(?:CRS\.Infrastructure|NPOI|Dapper|NLog|Microsoft\.Data\.Sqlite|System\.Net\.Http)\b')) "前台越过用例边界：$($file.Name)"
        Assert-Boundary (-not ($source -match '\bIDesktopOperations\b|\bICalculationRepository\b|\bFifoEngine\b|\bTaxEngine\b|File\.(Read|Write)')) "前台出现后台职责：$($file.Name)"
    }
}
[xml]$packages = Get-Content -Raw (Join-Path $RepositoryRoot 'Directory.Packages.props')
Assert-Boundary ($null -eq $packages.SelectSingleNode("//PackageVersion[@Include='AntdUI']")) '存在已移除前台的 AntdUI 依赖。'
$ui = $packages.SelectSingleNode("//PackageVersion[@Include='WPF-UI']")
Assert-Boundary ($ui.Version -match '^4\.3\.\d+$') 'WPF UI 必须使用 4.3.x。'
foreach ($package in @('CommunityToolkit.Mvvm','DataGridExtensions','LiveChartsCore.SkiaSharpView.WPF')) {
    Assert-Boundary ($null -ne $packages.SelectSingleNode("//PackageVersion[@Include='$package']")) "缺少指定包：$package"
}
foreach ($projectFile in Get-ChildItem (Join-Path $RepositoryRoot 'src'),(Join-Path $RepositoryRoot 'tests') -Filter '*.csproj' -Recurse | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]') {
    [xml]$project = Get-Content -Raw -LiteralPath $projectFile.FullName
    foreach ($reference in $project.SelectNodes('//ProjectReference')) {
        $target = [IO.Path]::GetFullPath((Join-Path $projectFile.DirectoryName $reference.Include))
        Assert-Boundary (Test-Path -LiteralPath $target -PathType Leaf) "项目引用失效：$($projectFile.Name) -> $target"
    }
    Assert-Boundary ($project.SelectNodes('//PackageReference[@Version]').Count -eq 0) "包版本未集中管理：$($projectFile.Name)"
}
foreach ($name in @('Dashboard','Import','Trades','Fifo','ExchangeRates','TaxCalculation','Reconciliation','Reports','History')) {
    Assert-Boundary (Test-Path (Join-Path $wpf "Views/Pages/${name}Page.xaml")) "缺少页面：$name"
}
Assert-Boundary (-not (Test-Path (Join-Path $RepositoryRoot 'CRS.Python'))) 'Python 版本仍存在。'
Assert-Boundary (Test-Path (Join-Path $RepositoryRoot 'CRS.sln')) '缺少统一解决方案。'
$solution = Get-Content -Raw (Join-Path $RepositoryRoot 'CRS.sln')
Assert-Boundary (-not (Test-Path (Join-Path $RepositoryRoot 'src/CRS.Desktop.WinForms'))) 'WinForms 项目仍存在。'
Assert-Boundary (-not ($solution -match 'CRS\.Desktop\.WinForms')) '解决方案仍引用 WinForms 项目。'
Write-Output 'Domain / Application / Infrastructure / 前台边界、页面、项目引用和包版本检查通过。'
