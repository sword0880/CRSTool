param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$baselinePath = Join-Path $PSScriptRoot 'wpf-color-baseline.json'
$baseline = Get-Content -Raw -LiteralPath $baselinePath | ConvertFrom-Json -AsHashtable
$palettePath = Join-Path $RepositoryRoot 'src/CRS.Desktop.Wpf/Styles/BrandPalette.xaml'
$stylesPath = Join-Path $RepositoryRoot 'src/CRS.Desktop.Wpf/Styles/SharedStyles.xaml'
$resourceText = (Get-Content -Raw -LiteralPath $palettePath) + "`n" + (Get-Content -Raw -LiteralPath $stylesPath)
$defined = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($found in [regex]::Matches($resourceText, 'x:Key="([^"]+)"')){
    [void]$defined.Add($found.Groups[1].Value)
}
$failures = [Collections.Generic.List[string]]::new()
$src = Join-Path $RepositoryRoot 'src'
$files = @(Get-ChildItem -Path $src -Filter '*.xaml' -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
})
foreach($file in $files) {
    $relative = [IO.Path]::GetRelativePath($RepositoryRoot,$file.FullName).Replace([IO.Path]::DirectorySeparatorChar,'/')
    $source = Get-Content -Raw -LiteralPath $file.FullName
    try {
        $null = [xml]$source
    } catch {
        $failures.Add("XML 无法解析：$relative")
        continue
    }
    # 调色板是唯一允许集中存放字面十六进制品牌色的位置。
    if($relative -eq 'src/CRS.Desktop.Wpf/Styles/BrandPalette.xaml') { continue }
    $hardcoded = [regex]::Matches($source, '#[A-Fa-f0-9]{6,8}\b').Count
    $legacyAllowance = 0
    if($baseline.ContainsKey($relative)) { $legacyAllowance = [int]$baseline[$relative] }
    if($hardcoded -gt $legacyAllowance) {
        $failures.Add("新增硬编码颜色：$relative，现有 $hardcoded / 基线 $legacyAllowance")
    }
    foreach($found in [regex]::Matches($source,'\{(?:DynamicResource|StaticResource)\s+((?:Brand|Ui)[A-Za-z0-9]+)\}')) {
        $key = $found.Groups[1].Value
        if(-not $defined.Contains($key)) { $failures.Add("共享视觉资源键不存在：$relative -> $key") }
    }
}
# 插件视觉契约只能消费宿主动态资源，不能依赖桌面实现程序集。
foreach($name in @('src/CRS.Plugin.Wpf/CRS.Plugin.Wpf.csproj','src/Plugins/CRS.Plugins.NationalTeamWatch/CRS.Plugins.NationalTeamWatch.csproj')) {
    $projectText = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot $name)
    if($projectText -match 'ProjectReference[^>]+CRS\.Desktop\.Wpf') {
        $failures.Add("插件反向引用桌面程序：$name")
    }
}
$contract = Get-Content -Raw -LiteralPath (Join-Path $RepositoryRoot 'src/CRS.Plugin.Wpf/PluginUiResources.cs')
foreach($found in [regex]::Matches($contract, '"((?:Brand|Ui)[A-Za-z0-9]+)"')) {
    if(-not $defined.Contains($found.Groups[1].Value)) {
        $failures.Add("插件 UI 契约要求的资源键缺失：$($found.Groups[1].Value)")
    }
}
if($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "WPF 视觉规范静态检查失败：$($failures.Count) 项。"
}
Write-Host "PASS WPF 视觉规范：$($files.Count) 个 XAML XML、共享资源键、硬编码颜色基线与插件依赖边界。"
