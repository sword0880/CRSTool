param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
# 仅复制元数据到测试新目录；负例不修改实际项目或文档。
$fixture = Join-Path $root ('.test_tmp-arch-20261016/graph-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'CRS.sln') -Destination $fixture
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'src'),(Join-Path $root 'tests') -Recurse -Filter '*.csproj' |
    Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]') {
    $target = Join-Path $fixture ([IO.Path]::GetRelativePath($root, $file.FullName))
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$document = Join-Path $fixture 'docs/architecture/CURRENT_ARCHITECTURE.md'
New-Item -ItemType Directory -Path (Split-Path $document -Parent) -Force | Out-Null
$originalDocument = Get-Content -Raw -LiteralPath (Join-Path $root 'docs/architecture/CURRENT_ARCHITECTURE.md')
Set-Content -LiteralPath $document -Value $originalDocument
$verify = Join-Path $PSScriptRoot 'Verify-ProjectGraph.ps1'
& $verify -RepositoryRoot $fixture
function Assert-Rejected([string]$Expected) {
    try { & $verify -RepositoryRoot $fixture; throw '未拒绝负例。' }
    catch { if (-not $_.Exception.Message.Contains($Expected)) { throw }; Write-Output "PASS 项目门禁拒绝：$Expected" }
}
$domain = Join-Path $fixture 'src/CRS.Domain/CRS.Domain.csproj'
$originalProject = Get-Content -Raw -LiteralPath $domain
[xml]$cycleProject = $originalProject
$group = $cycleProject.CreateElement('ItemGroup')
$reference = $cycleProject.CreateElement('ProjectReference')
$reference.SetAttribute('Include', '../CRS.Application/CRS.Application.csproj')
$group.AppendChild($reference) | Out-Null
$cycleProject.DocumentElement.AppendChild($group) | Out-Null
$cycleProject.Save($domain)
Assert-Rejected '循环依赖'
Set-Content -LiteralPath $domain -Value $originalProject
Set-Content -LiteralPath $document -Value ($originalDocument.Replace('| CRS.Tests.Background |', '| CRS.Tests.Removed |'))
Assert-Rejected '项目不一致'
Set-Content -LiteralPath $document -Value ($originalDocument.Replace('| CRS.Application | CRS.Common, CRS.Domain |', '| CRS.Application | 无 |'))
Assert-Rejected '直接依赖已漂移'
Set-Content -LiteralPath $document -Value $originalDocument
$extra = Join-Path $fixture 'src/CRS.Unregistered/CRS.Unregistered.csproj'
New-Item -ItemType Directory -Path (Split-Path $extra -Parent) -Force | Out-Null
Set-Content -LiteralPath $extra -Value '<Project Sdk="Microsoft.NET.Sdk" />'
Assert-Rejected '生产或测试项目未登记'
Write-Output '项目门禁：正常项目通过，四种结构回退均拒绝。'
