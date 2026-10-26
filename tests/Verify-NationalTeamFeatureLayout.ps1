$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent

function Get-DirectCodeFiles([string]$RelativePath, [string[]]$Extensions = @('.cs', '.xaml')) {
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        throw "Required NationalTeam feature directory is missing: $RelativePath"
    }
    return @(Get-ChildItem -LiteralPath $path -File | Where-Object { $Extensions -contains $_.Extension.ToLowerInvariant() })
}

function Assert-NoLooseCode([string]$RelativePath, [string[]]$Extensions = @('.cs', '.xaml')) {
    $files = Get-DirectCodeFiles $RelativePath $Extensions
    if ($files.Count -gt 0) {
        $names = ($files | ForEach-Object { $_.Name }) -join ', '
        throw ("Business source must be grouped into feature folders; found loose files in " + $RelativePath + ": " + $names)
    }
}

function Assert-OnlyExpectedRootCode([string]$RelativePath, [string[]]$AllowedNames) {
    $files = Get-DirectCodeFiles $RelativePath @('.cs', '.xaml')
    $unexpected = @($files | Where-Object { $_.Name -notin $AllowedNames })
    if ($unexpected.Count -gt 0) {
        throw ("Unexpected loose source files in " + $RelativePath + ": " + (($unexpected | ForEach-Object { $_.Name }) -join ', '))
    }
}

$core = 'src/Plugins/CRS.Plugins.NationalTeam.Core'
$jobs = 'src/Plugins/CRS.Plugins.NationalTeam.Jobs'
$ui = 'src/Plugins/CRS.Plugins.NationalTeamWatch'

Assert-NoLooseCode "$core/Domain"
Assert-NoLooseCode "$core/Application"
Assert-NoLooseCode $core
Assert-NoLooseCode "$jobs/Application"
Assert-NoLooseCode "$jobs/Infrastructure"
Assert-NoLooseCode $jobs
Assert-NoLooseCode "$ui/Presentation"
Assert-NoLooseCode "$ui/Application"
Assert-OnlyExpectedRootCode $ui @('GlobalUsings.cs', 'NationalTeamPluginEntry.cs')
Assert-OnlyExpectedRootCode "$jobs/Background" @('NationalTeamBackgroundEntry.cs')

# IPC客户端按功能归档，后台用例不实现这些客户端合同。
$workerRoot = Join-Path $repoRoot "$ui/Application/Worker"
$workerFiles = @(Get-ChildItem -LiteralPath $workerRoot -Recurse -File -Filter 'WorkerEtfWorkspace*.cs')
if ($workerFiles.Count -ne 7) {
    throw "Expected 7 WorkerEtfWorkspace source parts, found $($workerFiles.Count)."
}
foreach ($file in $workerFiles) {
    $relative = $file.FullName.Substring($repoRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar).Replace([string][IO.Path]::DirectorySeparatorChar, '/')
    if ($relative -notmatch '^src/Plugins/CRS\.Plugins\.NationalTeamWatch/Application/Worker/(Etf|Equity|ExitAlerts|IndustryValuation|MarketCycle|Nav)/WorkerEtfWorkspace[^/]*\.cs$') {
        throw ("Worker workspace partial is not grouped by feature: " + $relative)
    }
}

# WPF page and code-behind must move together so generated InitializeComponent resources remain coherent.
$xamlFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "$ui/Presentation") -Recurse -File -Filter '*.xaml')
foreach ($xaml in $xamlFiles) {
    if (-not (Test-Path -LiteralPath ($xaml.FullName + '.cs') -PathType Leaf)) {
        throw ("WPF XAML page is missing its paired code-behind: " + $xaml.FullName)
    }
}
$codeBehind = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot "$ui/Presentation") -Recurse -File -Filter '*.xaml.cs')
if ($codeBehind.Count -ne $xamlFiles.Count) {
    throw "WPF XAML/code-behind counts differ: XAML=$($xamlFiles.Count), code-behind=$($codeBehind.Count)."
}

# Shared storage remains intentionally shared; this guard is organizational, not a claim of module isolation.
$marketRepository = Join-Path $repoRoot "$jobs/Infrastructure/Shared/MarketRepository.cs"
if (-not (Test-Path -LiteralPath $marketRepository -PathType Leaf)) {
    throw 'The shared MarketRepository entry point must remain explicitly located under Infrastructure/Shared.'
}


# 页面依赖功能端口；组合接口仅用于装配，不保留重复旧契约。
$portFiles = @(
    "$core/Application/Shared/IWorkspaceAccess.cs",
    "$core/Application/Etf/IEtfResearchWorkspace.cs",
    "$core/Application/Equity/IDirectEquityWorkspace.cs",
    "$core/Application/IndustryValuation/IIndustryValuationWorkspace.cs",
    "$core/Application/MarketCycle/IMarketCycleWorkspace.cs",
    "$core/Application/Nav/INavWorkspace.cs",
    "$core/Application/ExitAlerts/IExitAlertWorkspace.cs"
)
foreach ($port in $portFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $port) -PathType Leaf)) {
        throw "Required narrow workspace port is missing: $port"
    }
    $contractSource = Get-Content -LiteralPath (Join-Path $repoRoot $port) -Raw
    if ($contractSource -match '=>|NotSupportedException|Task\.FromResult') {
        throw "Workspace contracts must require explicit implementations without default fallbacks: $port"
    }
}
$assemblyContract = Get-Content -LiteralPath (Join-Path $repoRoot "$core/Application/Shared/INationalTeamWorkspace.cs") -Raw
if ($assemblyContract -notmatch 'public interface INationalTeamWorkspace : IEtfResearchWorkspace' -or $assemblyContract -match 'Task<') {
    throw 'NationalTeam assembly contract must compose feature ports without duplicating their methods.'
}
if (Test-Path -LiteralPath (Join-Path $repoRoot "$core/Application/Shared/IEtfWorkspace.cs")) {
    throw 'The retired composite workspace interface must not return.'
}

# 后台用例与客户端合同分离；跨功能共享执行会话，不重建聚合partial。
$applicationRoot = Join-Path $repoRoot "$jobs/Application"
foreach ($file in Get-ChildItem -LiteralPath $applicationRoot -Recurse -File -Filter '*.cs') {
    $source = Get-Content -LiteralPath $file.FullName -Raw
    if ($source -match '\b(EtfWorkspace|INationalTeamWorkspace|IEtfResearchWorkspace|IDirectEquityWorkspace|IIndustryValuationWorkspace|IMarketCycleWorkspace|INavWorkspace|IExitAlertWorkspace)\b') {
        throw "Backend use cases must not implement client workspace contracts: $($file.Name)"
    }
    if ($file.Name -notin @('MarketApplicationSession.cs','NationalTeamApplication.cs') -and $source -match '\bIMarketRepository\b') {
        throw "Feature use cases must access typed repository ports through the shared session: $($file.Name)"
    }
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure") -Recurse -File -Filter '*.cs') {
    if ((Get-Content -LiteralPath $file.FullName -Raw) -match 'class NationalTeamBackgroundSession') {
        throw "Background orchestration must not be hidden inside Infrastructure: $($file.Name)"
    }
}
$useCaseSession = Get-Content -LiteralPath (Join-Path $applicationRoot 'Shared/MarketApplicationSession.cs') -Raw
if ($useCaseSession -notmatch 'MarketRepositoryCapabilities\.Validate' -or $useCaseSession -match 'sessions is null\?') {
    throw 'The shared application session must verify capabilities before publishing independent reads, without serial fallback.'
}
$viewModelPortChecks = @(
    @{ Path = "$ui/Presentation/Equity/EquityViewModel.cs"; Pattern = 'EquityViewModel\(IDirectEquityWorkspace' },
    @{ Path = "$ui/Presentation/IndustryValuation/IndustryResearchViewModel.cs"; Pattern = 'IndustryResearchViewModel\(IIndustryValuationWorkspace' },
    @{ Path = "$ui/Presentation/MarketCycle/IndexResearchViewModel.cs"; Pattern = 'IndexResearchViewModel\(IMarketCycleWorkspace' },
    @{ Path = "$ui/Presentation/ExitAlerts/ExitAlertViewModel.cs"; Pattern = 'ExitAlertViewModel\(IExitAlertWorkspace' },
    @{ Path = "$ui/Presentation/Etf/EtfViewModel.cs"; Pattern = 'IEtfResearchWorkspace workspace' }
)
foreach ($check in $viewModelPortChecks) {
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $check.Path) -Raw
    if ($source -notmatch $check.Pattern) {
        throw "ViewModel must depend on its feature port: $($check.Path)"
    }
}

# Industry PE is the first real storage extraction: the facade must delegate, and the store must not rejoin the legacy partial class.
$sessionContractPath = Join-Path $repoRoot "$jobs/Infrastructure/Shared/IMarketRepositorySession.cs"
$industryStorePath = Join-Path $repoRoot "$jobs/Infrastructure/IndustryValuation/IndustryPeStore.cs"
$industryFacadePath = Join-Path $repoRoot "$jobs/Infrastructure/IndustryValuation/IndustryPeRepository.cs"
$calendarStorePath = Join-Path $repoRoot "$jobs/Infrastructure/Shared/MarketCalendarStore.cs"
$navFacadePath = Join-Path $repoRoot "$jobs/Infrastructure/Nav/NavRepository.cs"
$navStorePath = Join-Path $repoRoot "$jobs/Infrastructure/Nav/NavStore.cs"
$calendarFacadePath = Join-Path $repoRoot "$jobs/Infrastructure/IndustryValuation/IndustryPeCalendar.cs"
$indexFacadePath = Join-Path $repoRoot "$jobs/Infrastructure/MarketCycle/IndexSeriesRepository.cs"
$indexStorePath = Join-Path $repoRoot "$jobs/Infrastructure/MarketCycle/IndexSeriesStore.cs"
$calendarPersistenceFacadePath = Join-Path $repoRoot "$jobs/Infrastructure/Shared/CalendarRepository.cs"
$activityFacadePath = Join-Path $repoRoot "$jobs/Infrastructure/MarketCycle/MarketActivityRepository.cs"
$activityStorePath = Join-Path $repoRoot "$jobs/Infrastructure/MarketCycle/MarketActivityStore.cs"
$alertOutboxStorePath = Join-Path $repoRoot "$jobs/Infrastructure/ExitAlerts/AlertOutboxStore.cs"
$alertEventStorePath = Join-Path $repoRoot "$jobs/Infrastructure/ExitAlerts/AlertEventStore.cs"
$alertDeliveryStorePath = Join-Path $repoRoot "$jobs/Infrastructure/ExitAlerts/AlertDeliveryStore.cs"
if (-not (Test-Path -LiteralPath $sessionContractPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $industryStorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $calendarStorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $indexStorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $activityStorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $navStorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $alertOutboxStorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $alertEventStorePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $alertDeliveryStorePath -PathType Leaf)) {
    throw 'Storage extraction requires the shared session, IndustryPeStore, MarketCalendarStore, IndexSeriesStore and MarketActivityStore.'
}
$sessionSource = Get-Content -LiteralPath $sessionContractPath -Raw
$storeSource = Get-Content -LiteralPath $industryStorePath -Raw
$facadeSource = Get-Content -LiteralPath $industryFacadePath -Raw
$repositorySource = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/Shared/MarketRepository.cs") -Raw
$calendarStoreSource = Get-Content -LiteralPath $calendarStorePath -Raw
$calendarFacadeSource = Get-Content -LiteralPath $calendarFacadePath -Raw
$indexFacadeSource = Get-Content -LiteralPath $indexFacadePath -Raw
$indexStoreSource = Get-Content -LiteralPath $indexStorePath -Raw
$calendarPersistenceFacadeSource = Get-Content -LiteralPath $calendarPersistenceFacadePath -Raw
$activityFacadeSource = Get-Content -LiteralPath $activityFacadePath -Raw
$activityStoreSource = Get-Content -LiteralPath $activityStorePath -Raw
$navFacadeSource = Get-Content -LiteralPath $navFacadePath -Raw
$navStoreSource = Get-Content -LiteralPath $navStorePath -Raw
$alertOutboxStoreSource = Get-Content -LiteralPath $alertOutboxStorePath -Raw
$alertEventStoreSource = Get-Content -LiteralPath $alertEventStorePath -Raw
$alertDeliveryStoreSource = Get-Content -LiteralPath $alertDeliveryStorePath -Raw
$alertDeliveryFacadeSource = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/ExitAlerts/AlertStore.Delivery.cs") -Raw
$alertLifecycleSource = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/ExitAlerts/AlertStore.Lifecycle.cs") -Raw
$alertFacadeSource = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/ExitAlerts/AlertStore.cs") -Raw
if ($sessionSource -notmatch 'internal interface IMarketRepositorySession' -or
    $repositorySource -notmatch 'MarketDatabaseSession database' -or
    $facadeSource -notmatch 'industryPeStore\.IndustryPeRevisions' -or
    $storeSource -match 'partial class MarketRepository' -or
    $calendarFacadeSource -notmatch 'marketCalendarStore\.IndustryPeCalendar' -or
    $calendarPersistenceFacadeSource -notmatch 'marketCalendarStore\.RegisterReviewedCalendar' -or
    $calendarPersistenceFacadeSource -notmatch 'marketCalendarStore\.IsReviewedTradingDay' -or
    $indexFacadeSource -notmatch 'marketCalendarStore\.ReadPublishedTradingCalendar' -or
    $indexFacadeSource -notmatch 'indexSeriesStore\.IndexSeriesRevisions' -or
    $indexStoreSource -match 'partial class MarketRepository' -or
    $activityFacadeSource -notmatch 'marketActivityStore\.MarketActivity' -or
    $activityStoreSource -match 'partial class MarketRepository' -or
    $navFacadeSource -notmatch 'navStore\.NavPoints' -or
    $navStoreSource -match 'partial class MarketRepository' -or
    $alertFacadeSource -notmatch 'alertOutboxStore\.AlertOutbox' -or
    $alertOutboxStoreSource -match 'partial class MarketRepository' -or
    $alertOutboxStoreSource -match 'trustedCondition' -or
    $alertEventStoreSource -match 'partial class MarketRepository' -or
    $alertEventStoreSource -notmatch 'public void RecordChange' -or
    $alertFacadeSource -notmatch 'alertEventStore\.AlertEvents' -or
    $alertLifecycleSource -notmatch 'alertEventStore\.RecordChange\(row,kind\)' -or
    $alertDeliveryStoreSource -match 'partial class MarketRepository' -or
    $alertDeliveryFacadeSource -notmatch 'alertDeliveryStore\.CompleteAlertNotification' -or
    $alertDeliveryFacadeSource -notmatch 'alertDeliveryStore\.DueNotifications' -or
    $calendarStoreSource -match 'partial class MarketRepository') {
    throw 'Industry PE, shared calendar, index-series and market-activity storage must stay behind explicit sessions and feature stores.'
}
# 已迁移领域的总仓储入口只允许转发，禁止SQL重新混入兼容入口。
foreach ($feature in @('Equity','ExitAlerts','Evidence','Etf')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/$feature") -Filter '*Repository.cs') {
        $source = Get-Content -LiteralPath $file.FullName -Raw
        if ($source -match '\b(SELECT|INSERT|UPDATE|DELETE|CREATE TABLE)\b') { throw "Feature SQL leaked back into facade: $($file.Name)" }
    }
}
$databaseSession = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/Storage/MarketDatabaseSession.cs") -Raw
$etfStore = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/Etf/EtfStore.Session.cs") -Raw
$plainSqlite = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/Storage/PublicResearchSqlite.cs") -Raw
if ($databaseSession -notmatch 'IMarketRepositorySession,IDisposable' -or $databaseSession -match 'MarketRepository owner' -or
    $repositorySource -match 'new SqliteConnection|db\.Open|db\.Dispose|CREATE TABLE|PRAGMA|MarketRepositorySession' -or
    $etfStore -notmatch 'IMarketRepositorySession session' -or $etfStore -match 'MarketRepository owner' -or
    $plainSqlite -match 'Password\s*=' -or $databaseSession -match 'Password\s*=') {
    throw 'ETF storage and connection ownership must remain separate; public research databases cannot use encryption passwords.'
}
$integrityStore = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/Evidence/EvidenceIntegrityStore.cs") -Raw
$integrityFacade = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/Evidence/EvidenceIntegrityRepository.cs") -Raw
if ($integrityStore -match 'partial class MarketRepository' -or $integrityStore -notmatch 'IMarketRepositorySession session' -or
    $integrityFacade -notmatch 'evidenceIntegrityStore\.ReconcileEvidenceIntegrity' -or $repositorySource -match 'private .*ScanEvidence|private .*ApplyIntegrityIds') {
    throw 'Evidence integrity must remain an independent store with a forwarding facade.'
}
$alertSession = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/ExitAlerts/AlertStore.Session.cs") -Raw
if ($alertSession -notmatch 'IAlertMarketFacts facts' -or $alertSession -match '\bMarketRepository owner') {
    throw 'Alert feature must consume narrow market facts, not the aggregate repository.'
}
$equitySession = Get-Content -LiteralPath (Join-Path $repoRoot "$jobs/Infrastructure/Equity/EquityStore.Session.cs") -Raw
if ($equitySession -notmatch 'EquityStore\(IMarketRepositorySession session\)') { throw 'Equity feature session boundary missing.' }
$etfViewModelSource = Get-Content -LiteralPath (Join-Path $repoRoot "$ui/Presentation/Etf/EtfViewModel.cs") -Raw
$marketActivitySource = Get-Content -LiteralPath (Join-Path $repoRoot "$ui/Presentation/MarketCycle/MarketActivityPresentation.cs") -Raw
$proofViewModelSource = Get-Content -LiteralPath (Join-Path $repoRoot "$ui/Presentation/ExitAlerts/ExitAlertProofViewModel.cs") -Raw
if ($etfViewModelSource -notmatch 'IMarketCycleWorkspace marketCycleWorkspace' -or
    $marketActivitySource -notmatch 'marketCycleWorkspace\.MarketActivityAsync' -or
    $proofViewModelSource -notmatch 'etfWorkspace\.ConfigurationAsync') {
    throw 'ETF, market-cycle and exit-alert UI dependencies must use explicit feature ports.'
}
Write-Host "PASS NationalTeam feature layout: Core, Jobs, WPF presentation and Worker IPC files are grouped by feature."
Write-Host "Validated Worker workspace parts: $($workerFiles.Count); WPF pages: $($xamlFiles.Count); narrow feature ports: $($portFiles.Count)."
