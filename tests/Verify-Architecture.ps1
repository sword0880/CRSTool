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
        Assert-Boundary (-not ($source -match '\b(PerformanceCalculator|PerformanceCostCalculator|PerformanceCurveCalculator|XirrSolver)\s*\.')) "前台直接运行绩效算法：$($file.Name)"
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
foreach ($name in @('Dashboard','Import','Trades','Fifo','ExchangeRates','TaxCalculation','Reconciliation','Reports','History','Performance')) {
    Assert-Boundary (Test-Path (Join-Path $wpf "Views/Pages/${name}Page.xaml")) "缺少页面：$name"
}
Assert-Boundary (-not (Test-Path (Join-Path $RepositoryRoot 'CRS.Python'))) 'Python 版本仍存在。'
Assert-Boundary (Test-Path (Join-Path $RepositoryRoot 'CRS.sln')) '缺少统一解决方案。'
$solution = Get-Content -Raw (Join-Path $RepositoryRoot 'CRS.sln')
Assert-Boundary (-not (Test-Path (Join-Path $RepositoryRoot 'src/CRS.Desktop.WinForms'))) 'WinForms 项目仍存在。'
Assert-Boundary (-not ($solution -match 'CRS\.Desktop\.WinForms')) '解决方案仍引用 WinForms 项目。'
Write-Output 'Domain / Application / Infrastructure / 前台边界、页面、项目引用和包版本检查通过。'
foreach ($projectFile in Get-ChildItem (Join-Path $RepositoryRoot 'src') -Filter '*.csproj' -Recurse | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]') {
    [xml]$project = Get-Content -Raw $projectFile.FullName
    foreach ($reference in $project.SelectNodes('//ProjectReference')) {
        if ($projectFile.FullName -notmatch '[\\/]Plugins[\\/]') {
            Assert-Boundary ($reference.Include -notmatch 'Plugins|CRS\.Plugins\.') "宿主/税务静态引用业务插件：$($projectFile.Name)"
        } else {
            Assert-Boundary ($reference.Include -match 'CRS\.Plugin\.(Wpf|Abstractions)/|CRS\.Worker\.Abstractions/') "业务插件反向依赖宿主或税务：$($projectFile.Name)"
        }
    }
}
[xml]$contract = Get-Content -Raw (Join-Path $RepositoryRoot 'src/CRS.Plugin.Abstractions/CRS.Plugin.Abstractions.csproj')
Assert-Boundary ($contract.SelectNodes('//ProjectReference | //PackageReference').Count -eq 0) 'BCL插件契约不得引用实现或WPF。'
foreach($layer in @('Presentation','Domain')) {
    foreach($file in Get-ChildItem (Join-Path $RepositoryRoot "src/Plugins/CRS.Plugins.NationalTeamWatch/$layer") -Filter '*.cs' -Recurse) {
        $source=Get-Content -Raw $file.FullName
        Assert-Boundary (-not ($source -match 'Microsoft\.Data\.Sqlite|System\.Net\.Http|new\s+MarketRepository|\.Calculate\(')) "插件展示/领域层越界：$($file.Name)"
    }
}
Write-Output '插件契约、宿主与业务插件依赖门禁通过。'
# 后台链路必须可以脱离 WindowsDesktop/WPF 和个人税务程序集发布。
foreach($name in @('Abstractions','PluginHost','Ipc','Service')){
    $path=Join-Path $RepositoryRoot "src/CRS.Worker.$name/CRS.Worker.$name.csproj"
    [xml]$project=Get-Content -Raw $path
    Assert-Boundary ($null -eq $project.SelectSingleNode('//UseWPF | //UseWindowsForms')) "后台项目引用桌面框架：$name"
    foreach($reference in $project.SelectNodes('//ProjectReference')){
        Assert-Boundary ($reference.Include -match 'CRS\.Worker\.') "后台项目越过服务边界：$name"
    }
}
[xml]$backgroundContract=Get-Content -Raw (Join-Path $RepositoryRoot 'src/CRS.Worker.Abstractions/CRS.Worker.Abstractions.csproj')
Assert-Boundary ($backgroundContract.SelectNodes('//ProjectReference | //PackageReference').Count -eq 0) '后台契约必须保持纯BCL。'
Write-Output '后台服务无 WPF/私人税务依赖门禁通过。'
foreach($file in Get-ChildItem (Join-Path $RepositoryRoot 'src/Plugins/CRS.Plugins.NationalTeamWatch/Application') -Filter '*.cs' -Recurse) {
    $source=Get-Content -Raw $file.FullName
    Assert-Boundary (-not ($source -match 'CRS\.Plugins\.NationalTeamWatch\.Infrastructure|Microsoft\.Data\.Sqlite|new\s+MarketRepository|\bFile\.(Read|Write)')) "插件用例层引用基础设施实现：$($file.Name)"
}
# 先完成 Release 构建；以 SDK Roslyn 语法树检查绩效模块的新方法和已评审复杂度增量。
& dotnet run --project (Join-Path $RepositoryRoot 'tests/CRS.Tests.Domain') -c Release --no-build -- --performance-architecture $RepositoryRoot
Assert-Boundary ($LASTEXITCODE -eq 0) '绩效复杂度/方法规模门禁失败（请先构建当前源码）。'
