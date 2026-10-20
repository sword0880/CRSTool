#requires -Version 5.1
# 静态视觉防回退：扫描桌面和插件 XAML，阻止重新散落旧蓝色强调色。
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$palettePath = Join-Path $root "src/CRS.Desktop.Wpf/Styles/BrandPalette.xaml"
[xml]$palette = Get-Content -LiteralPath $palettePath -Raw -Encoding UTF8
$xamlNs = "http://schemas.microsoft.com/winfx/2006/xaml"
$keys = [System.Collections.Generic.HashSet[string]]::new()
foreach ($element in $palette.DocumentElement.ChildNodes) {
    if ($element.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
    $key = $element.GetAttribute("Key", $xamlNs)
    if ($key) { [void]$keys.Add($key) }
}
$pattern = '(?:StaticResource|DynamicResource)\s+(Brand[A-Za-z]+Brush)'
$legacy = '#(?:2563EB|EFF6FF|DBEAFE|F0F5FF|1E3A8A)'
$failures = [System.Collections.Generic.List[string]]::new()
$files = Get-ChildItem -Path (Join-Path $root "src") -Filter "*.xaml" -File -Recurse
foreach ($file in $files) {
    $body = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    try { [void]([xml]$body) }
    catch { $failures.Add("XAML XML error: $($file.FullName): $_") }
    if ($file.FullName -eq $palettePath) { continue }
    foreach ($match in [regex]::Matches($body, $pattern)) {
        $key = $match.Groups[1].Value
        if (-not $keys.Contains($key)) { $failures.Add("Unknown resource $key in $($file.FullName)") }
    }
    if ($body -match $legacy) { $failures.Add("Legacy blue accent in $($file.FullName)") }
}
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}
Write-Host "Brand theme verification passed: $($files.Count) XAML files, $($keys.Count) design tokens."
