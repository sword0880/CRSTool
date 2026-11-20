param([Parameter(Mandatory)][string]$OutputDirectory,[ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw '雪球观点追踪包需要新的输出目录。' }
$projectRoot = Join-Path $repo 'src/Plugins/XueqiuWatch'
foreach ($project in @('CRS.Plugins.XueqiuWatch.Jobs','CRS.Plugins.XueqiuWatch.Wpf')) {
    & dotnet publish (Join-Path $projectRoot "$project/$project.csproj") -c $Configuration --no-self-contained --no-restore -m:1 -o $output
    if ($LASTEXITCODE -ne 0) { throw "雪球观点追踪入口发布失败：$project" }
}
