param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# 从解决方案与项目文件读取实际依赖；不依赖手写的项目数量或允许循环列表。
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$solution = Get-Content -Raw -LiteralPath (Join-Path $root 'CRS.sln')
$graph = @{}
$names = @{}
foreach ($match in [regex]::Matches($solution, '(?m)^Project\("[^"]+"\) = "([^"]+)", "([^"]+\.csproj)"')) {
    $name = $match.Groups[1].Value
    $path = [IO.Path]::GetFullPath((Join-Path $root $match.Groups[2].Value))
    if ($names.ContainsKey($name) -or $graph.ContainsKey($path)) { throw "解决方案项目重复：$name" }
    [xml]$project = Get-Content -Raw -LiteralPath $path
    $names[$name] = $path
    $graph[$path] = @($project.SelectNodes('//ProjectReference') | ForEach-Object {
        [IO.Path]::GetFullPath((Join-Path (Split-Path $path -Parent) $_.Include))
    })
}
if ($graph.Count -eq 0) { throw '未读取到解决方案项目。' }
foreach ($path in $graph.Keys) {
    foreach ($dependency in $graph[$path]) {
        if (-not $graph.ContainsKey($dependency)) { throw "引用项目未登记在解决方案：$dependency" }
    }
}
foreach ($productionFile in Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.csproj' -Recurse |
    Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]') {
    if (-not $graph.ContainsKey($productionFile.FullName)) { throw "生产项目未登记在解决方案：$($productionFile.Name)" }
}
$states = @{}
function Visit-Project([string]$Path, [string[]]$Stack) {
    if ($states[$Path] -eq 'Visiting') { throw ('项目循环依赖：' + (($Stack + $Path) -join ' -> ')) }
    if ($states[$Path] -eq 'Visited') { return }
    $states[$Path] = 'Visiting'
    foreach ($dependency in $graph[$Path]) { Visit-Project $dependency ($Stack + $Path) }
    $states[$Path] = 'Visited'
}
foreach ($path in $graph.Keys) { Visit-Project $path @() }
$document = Get-Content -Raw -LiteralPath (Join-Path $root 'docs/architecture/CURRENT_ARCHITECTURE.md')
$documented = @([regex]::Matches($document, '(?m)^\| (CRS\.[A-Za-z0-9.]+) \|') | ForEach-Object { $_.Groups[1].Value })
if (@($documented | Select-Object -Unique).Count -ne $documented.Count) { throw '当前架构文档项目重复。' }
$difference = @(Compare-Object @($names.Keys | Sort-Object) @($documented | Sort-Object))
if ($difference.Count -ne 0) { throw ('当前架构文档与解决方案项目不一致：' + (($difference | ForEach-Object InputObject) -join ', ')) }
foreach ($row in [regex]::Matches($document, '(?m)^\| (CRS\.[A-Za-z0-9.]+) \| ([^|]+) \|')) {
    $name = $row.Groups[1].Value
    if (-not $names[$name].StartsWith((Join-Path $root 'src') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $declared = $row.Groups[2].Value.Trim()
    [xml]$project = Get-Content -Raw -LiteralPath $names[$name]
    $expected = @($project.SelectNodes('//ProjectReference') |
        Where-Object { $_.GetAttribute('ReferenceOutputAssembly') -ne 'false' } |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Include) } | Sort-Object)
    $actual = @(if ($declared -ne '无') { $declared.Split(',').Trim() | Sort-Object })
    if (($expected -join ',') -ne ($actual -join ',')) { throw "当前架构文档的直接依赖已漂移：$name" }
}
Write-Output "解决方案 $($graph.Count) 个项目清单一致、生产项目完整、无循环依赖。"
