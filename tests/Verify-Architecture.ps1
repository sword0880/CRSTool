param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Verify-ProjectGraph.ps1') -RepositoryRoot $RepositoryRoot
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
            Assert-Boundary ($reference.Include -match 'CRS\.Plugin\.(Wpf|Abstractions)/|CRS\.Worker\.(Abstractions|Ipc)/|CRS\.Plugins\.NationalTeam\.Core/') "业务插件反向依赖宿主或税务：$($projectFile.Name)"
        }
    }
}
[xml]$contract = Get-Content -Raw (Join-Path $RepositoryRoot 'src/CRS.Plugin.Abstractions/CRS.Plugin.Abstractions.csproj')
Assert-Boundary ($contract.SelectNodes('//ProjectReference | //PackageReference').Count -eq 0) 'BCL插件契约不得引用实现或WPF。'
foreach($sourceDir in @('src/Plugins/CRS.Plugins.NationalTeamWatch/Presentation','src/Plugins/CRS.Plugins.NationalTeam.Core/Domain')) {
    foreach($file in Get-ChildItem (Join-Path $RepositoryRoot $sourceDir) -Filter '*.cs' -Recurse) {
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
        # 公共通知只在服务组合根装配；其它后台层不能反向依赖通知实现。
        $sharedNotification=$name -eq 'Service' -and $reference.Include -eq '../CRS.Notifications/CRS.Notifications.csproj'
        Assert-Boundary ($reference.Include -match 'CRS\.Worker\.' -or $sharedNotification) "后台项目越过服务边界：$name"
    }
}
[xml]$backgroundContract=Get-Content -Raw (Join-Path $RepositoryRoot 'src/CRS.Worker.Abstractions/CRS.Worker.Abstractions.csproj')
Assert-Boundary ($backgroundContract.SelectNodes('//ProjectReference | //PackageReference').Count -eq 0) '后台契约必须保持纯BCL。'
# 通知组件保持独立，SMTP依赖只能在这里出现，宿主仅保留契约适配。
[xml]$notificationProject=Get-Content -Raw (Join-Path $RepositoryRoot 'src/CRS.Notifications/CRS.Notifications.csproj')
Assert-Boundary ($notificationProject.SelectNodes('//ProjectReference | //UseWPF | //UseWindowsForms').Count -eq 0) '公共通知组件不得依赖宿主、业务或UI。'
foreach($file in Get-ChildItem (Join-Path $RepositoryRoot 'src/CRS.Notifications') -Filter '*.cs' -Recurse | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]'){
    Assert-Boundary (-not ((Get-Content -Raw $file.FullName) -match 'using CRS\.(Worker|Plugins|Infrastructure|Application)|using System\.Windows')) '公共通知组件反向依赖业务或宿主。'
}
foreach($file in Get-ChildItem (Join-Path $RepositoryRoot 'src/CRS.Worker.Service') -Filter '*.cs' -Recurse | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]'){
    Assert-Boundary (-not ((Get-Content -Raw $file.FullName) -match 'using (MailKit|MimeKit|System\.Net\.Mail)')) 'SMTP实现必须归属公共通知组件。'
}
Write-Output '后台服务无 WPF/私人税务依赖门禁通过。'
# ETF桌面入口必须只使用管道，后台入口必须保持无界面，主程序会话不能再实例化调度器。
[xml]$etfUi=Get-Content -Raw (Join-Path $RepositoryRoot 'src/Plugins/CRS.Plugins.NationalTeamWatch/CRS.Plugins.NationalTeamWatch.csproj')
Assert-Boundary ($etfUi.SelectNodes('//PackageReference').Include -notcontains 'Microsoft.Data.Sqlite.Core') 'ETF桌面引用了市场库连接实现。'
Assert-Boundary (-not (($etfUi.SelectNodes('//ProjectReference').Include -join ';') -match 'NationalTeam.Jobs')) 'ETF桌面反向引用了采集作业。'
[xml]$etfJobs=Get-Content -Raw (Join-Path $RepositoryRoot 'src/Plugins/CRS.Plugins.NationalTeam.Jobs/CRS.Plugins.NationalTeam.Jobs.csproj')
Assert-Boundary ($null -eq $etfJobs.SelectSingleNode('//UseWPF | //UseWindowsForms')) 'ETF作业引用了桌面框架。'
Assert-Boundary (-not (($etfJobs.SelectNodes('//ProjectReference').Include -join ';') -match 'Plugin.Wpf|Worker.Ipc|NationalTeamWatch/')) 'ETF作业反向引用了桌面实现。'
Assert-Boundary ($etfJobs.SelectNodes('//Compile[@Include]').Count -eq 0 -and $etfUi.SelectNodes('//Compile[@Remove]').Count -eq 0) '国家队 Core/Jobs/UI 必须实体拥有源码，不得通过链接/排除维持虚假模块边界。'
[xml]$etfCore=Get-Content -Raw (Join-Path $RepositoryRoot 'src/Plugins/CRS.Plugins.NationalTeam.Core/CRS.Plugins.NationalTeam.Core.csproj')
Assert-Boundary ($etfCore.SelectNodes('//Compile[@Include]').Count -eq 0) 'ETF Core 不允许跨目录源码链接。'
Assert-Boundary (-not (Test-Path (Join-Path $RepositoryRoot 'src/CRS.PluginHost/QuartzTaskScheduler.cs'))) '桌面宿主仍保留第二套 Quartz 执行实现。'
$uiEntry=Get-Content -Raw (Join-Path $RepositoryRoot 'src/Plugins/CRS.Plugins.NationalTeamWatch/NationalTeamPluginEntry.cs')
Assert-Boundary (-not ($uiEntry -match 'new\s+(MarketRepository|OfficialShareSources)|RegisterAsync|\bEtfWorkspace\(')) 'ETF桌面入口仍有本地采集/写入路径。'
$uiHost=Get-Content -Raw (Join-Path $RepositoryRoot 'src/CRS.PluginHost/PluginRuntime.cs')
Assert-Boundary (-not ($uiHost -match 'new\s+QuartzTaskScheduler|scheduler\.Scope')) '桌面插件宿主仍创建第二个调度器。'
Write-Output 'ETF双入口、唯一任务执行者与桌面无写库依赖门禁通过。'
foreach($sourceDir in @('src/Plugins/CRS.Plugins.NationalTeam.Core/Application','src/Plugins/CRS.Plugins.NationalTeam.Jobs/Application')) {
foreach($file in Get-ChildItem (Join-Path $RepositoryRoot $sourceDir) -Filter '*.cs' -Recurse) {
    $source=Get-Content -Raw $file.FullName
    Assert-Boundary (-not ($source -match 'CRS\.Plugins\.NationalTeamWatch\.Infrastructure|Microsoft\.Data\.Sqlite|new\s+MarketRepository|\bFile\.(Read|Write)')) "插件用例层引用基础设施实现：$($file.Name)"
}
}
# 先完成 Release 构建；以 SDK Roslyn 语法树检查绩效模块的新方法和已评审复杂度增量。
& dotnet run --project (Join-Path $RepositoryRoot 'tests/CRS.Tests.Domain') -c Release --no-build -- --performance-architecture $RepositoryRoot
Assert-Boundary ($LASTEXITCODE -eq 0) '绩效复杂度/方法规模门禁失败（请先构建当前源码）。'
