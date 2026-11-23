param([Parameter(Mandatory)][string]$OutputDirectory,[ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$BackgroundOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw '雪球观点追踪包需要新的输出目录。' }
$projectRoot = Join-Path $repo 'src/Plugins/XueqiuWatch'
$projects = @('CRS.Plugins.XueqiuWatch.Jobs')
# 浏览器依赖仅供桌面配置订阅，后台包只发布 RSS 采集入口。
if (!$BackgroundOnly) { $projects += 'CRS.Plugins.XueqiuWatch.Wpf' }
foreach ($project in $projects) {
    & dotnet publish (Join-Path $projectRoot "$project/$project.csproj") -c $Configuration --no-self-contained --no-restore -m:1 -o $output
    if ($LASTEXITCODE -ne 0) { throw "雪球观点追踪入口发布失败：$project" }
}
if ($BackgroundOnly) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'plugin.json') -Destination $output
}
