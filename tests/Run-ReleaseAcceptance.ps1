param([switch]$SkipBuild, [switch]$Smoke, [switch]$ReuseBenchmark, [string]$CoverageTool, [string]$RealManifest,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/acceptance'))
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$oldTemp = $env:TEMP; $oldTmp = $env:TMP; $oldTelemetry = $env:DOTNET_COVERAGE_TELEMETRY_OPTOUT
$env:DOTNET_COVERAGE_TELEMETRY_OPTOUT = '1'
# 所有测试只使用隔离数据；不删除用户数据库、报告及既有验收产物。
$temporary = Join-Path $output ('temp-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temporary | Out-Null
$env:TEMP = $temporary; $env:TMP = $temporary
$checks = [ordered]@{}
try {
    if (-not $SkipBuild) {
        & dotnet build (Join-Path $repository 'CRS.sln') -c Release -m:1 *> (Join-Path $output 'build.log')
        if ($LASTEXITCODE -ne 0) { throw '发布构建失败，请查看 build.log。' }
        $checks.Build = 'passed'
    } else { $checks.Build = 'skipped_existing_release_binaries' }
    & (Join-Path $PSScriptRoot 'Verify-Architecture.ps1') -RepositoryRoot $repository *> (Join-Path $output 'architecture.log')
    $checks.Architecture = 'passed'
    $collector = $(if ($CoverageTool) {[IO.Path]::GetFullPath($CoverageTool)} else {Join-Path $repository '.tools/dotnet-coverage.exe'})
    if (-not (Test-Path -LiteralPath $collector)) {
        $command = Get-Command dotnet-coverage -ErrorAction SilentlyContinue
        $collector = $(if ($command) { $command.Source } else { $null })
    }
    if (-not $collector) {
        # NuGet 下载不可用时允许使用已安装 Visual Studio 的官方覆盖率命令行工具。
        $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path -LiteralPath $locator) {
            $found = @(& $locator -latest -products '*' -find 'Common7/IDE/Extensions/Microsoft/CodeCoverage.Console/Microsoft.CodeCoverage.Console.exe')
            if ($found.Count -gt 0) { $collector=$found[0] }
        }
    }
    $coverageFiles = @()
    foreach ($name in @('Domain','Application','Security','Infrastructure','Wpf','Verification')) {
        $framework = $(if ($name -eq 'Wpf') {'net10.0-windows10.0.19041.0'} else {'net10.0'})
        $binary = Join-Path $repository "tests/CRS.Tests.$name/bin/Release/$framework/CRS.Tests.$name.dll"
        $log = Join-Path $output "$name.log"
        if ($collector) {
            $coverageFile = Join-Path $output "$name.coverage"
            & $collector collect -s (Join-Path $PSScriptRoot 'coverage.settings.xml') -o $coverageFile dotnet $binary *> $log
            $coverageFiles += $coverageFile
        } else { & dotnet $binary *> $log }
        if ($LASTEXITCODE -ne 0) { throw "$name 回归失败，请查看 $log。" }
        $checks[$name] = 'passed'
        Get-Content -LiteralPath $log | Select-Object -Last 2 | Write-Output
    }
    if ($ReuseBenchmark) {
        if (-not (Test-Path -LiteralPath (Join-Path $output 'benchmark.json'))) { throw '没有可复用的性能报告。' }
        $checks.Benchmark = 'existing_measurement_reused_check_original_timestamp'
    } else {
        & dotnet (Join-Path $repository 'tests/CRS.Tests.Verification/bin/Release/net10.0/CRS.Tests.Verification.dll') --benchmark $output *> (Join-Path $output 'benchmark.log')
        if ($LASTEXITCODE -ne 0) { throw '性能样本正确性验证失败，请查看 benchmark.log。' }
        $checks.Benchmark = 'measured_synthetic_no_threshold_agreed'
    }
    & (Join-Path $PSScriptRoot 'Collect-DependencyLicenses.ps1') -OutputDirectory (Join-Path $output 'licenses')
    $checks.DependencyLicenses = 'inventory_collected_distribution_review_pending'
    $checks.ProjectLicense = $(if (Test-Path -LiteralPath (Join-Path $repository 'LICENSE')) {'present_review_pending'} else {'missing_owner_decision_required'})
    $checks.Privacy = 'automated_local_storage_and_proof_tests_passed_release_review_pending'
    if ($RealManifest) {
        & dotnet (Join-Path $repository 'tests/CRS.Tests.Verification/bin/Release/net10.0/CRS.Tests.Verification.dll') --accept-real $RealManifest $output *> (Join-Path $output 'real-sample.log')
        if ($LASTEXITCODE -ne 0) { throw '真实全年样本验收不通过，请查看 real-sample.log。' }
        $checks.RealSample = 'expected_values_matched_provenance_owner_attested'
    } else { $checks.RealSample = 'not_provided' }
    if ($Smoke) {
        $binary = Join-Path $repository 'src/CRS.Desktop.Wpf/bin/Release/net10.0-windows10.0.19041.0/CRS.Desktop.Wpf.dll'
        $fixture = Join-Path $repository 'tests/fixtures/ibkr_activity.xml'
        if ($collector) {
            $coverageFile=Join-Path $output 'WpfSmoke.coverage'
            & $collector collect -s (Join-Path $PSScriptRoot 'coverage.settings.xml') -o $coverageFile dotnet $binary --smoke-test --fixture $fixture --image (Join-Path $output 'wpf.png') *> (Join-Path $output 'wpf-smoke.log')
            $coverageFiles += $coverageFile
        } else { & dotnet $binary --smoke-test --fixture $fixture --image (Join-Path $output 'wpf.png') *> (Join-Path $output 'wpf-smoke.log') }
        if ($LASTEXITCODE -ne 0) { throw 'WPF 窗口验证失败。' }
        $checks.WpfSmoke = 'passed'
    } else { $checks.WpfSmoke = 'not_run' }
    if ($collector) {
        & $collector merge @coverageFiles -f cobertura -o (Join-Path $output 'coverage.cobertura.xml') *> (Join-Path $output 'coverage.log')
        if ($LASTEXITCODE -ne 0) { throw '覆盖率合并失败。' }
        [xml]$coverage = Get-Content -LiteralPath (Join-Path $output 'coverage.cobertura.xml') -Raw
        $checks.Coverage = [ordered]@{Status='collected_no_acceptance_threshold_agreed'; Tool=[IO.Path]::GetFileName($collector)
            LinesCovered=$coverage.coverage.'lines-covered'; LinesValid=$coverage.coverage.'lines-valid'; LineRate=$coverage.coverage.'line-rate'}
    } else { $checks.Coverage = 'not_collected_tool_unavailable' }
} catch {
    $checks.Failure = $_.Exception.Message
    throw
} finally {
    # 未提供的凭证、未同意的阈值和法律审核不能由脚本自动宣称完成。
    [ordered]@{ Schema='CRS.ReleaseAcceptance.v1'; CreatedUtc=[DateTimeOffset]::UtcNow; ReleaseApproved=$false; Checks=$checks } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'acceptance.json') -Encoding utf8
    $env:TEMP=$oldTemp; $env:TMP=$oldTmp; $env:DOTNET_COVERAGE_TELEMETRY_OPTOUT=$oldTelemetry
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ($resolved.StartsWith($output + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output "验收结果已保存到 $output；请核对 acceptance.json 中的未完成门槛。"
