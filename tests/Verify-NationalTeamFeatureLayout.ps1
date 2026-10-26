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

# The IPC workspace is still one compatibility facade, but each feature partial belongs to its own folder.
$workerRoot = Join-Path $repoRoot "$ui/Application/Worker"
$workerFiles = @(Get-ChildItem -LiteralPath $workerRoot -Recurse -File -Filter 'WorkerEtfWorkspace*.cs')
if ($workerFiles.Count -ne 6) {
    throw "Expected 6 WorkerEtfWorkspace source parts, found $($workerFiles.Count)."
}
foreach ($file in $workerFiles) {
    $relative = $file.FullName.Substring($repoRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar).Replace([string][IO.Path]::DirectorySeparatorChar, '/')
    if ($relative -notmatch '^src/Plugins/CRS\.Plugins\.NationalTeamWatch/Application/Worker/(Etf|Equity|ExitAlerts|IndustryValuation|MarketCycle)/WorkerEtfWorkspace[^/]*\.cs$') {
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

Write-Host "PASS NationalTeam feature layout: Core, Jobs, WPF presentation and Worker IPC files are grouped by feature."
Write-Host "Validated Worker workspace parts: $($workerFiles.Count); WPF pages: $($xamlFiles.Count)."
