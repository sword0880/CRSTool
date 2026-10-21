param([Parameter(Mandatory)][string]$AssemblyList, [Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Split-Path $output -Leaf) -ne 'Plugins') { throw '开发插件输出必须是 Plugins 目录。' }
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
function Assert-NoLinks([string]$Path) {
    for ($parent = [IO.DirectoryInfo]::new($Path); $null -ne $parent; $parent = $parent.Parent) {
        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "插件路径包含链接：$Path" }
    }
    if (Test-Path -LiteralPath $Path) {
        if (Get-ChildItem -LiteralPath $Path -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
            throw "插件目录包含链接：$Path"
        }
    }
}
$assemblies = @(Get-Content -LiteralPath $AssemblyList -Encoding UTF8 | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath($_) })
$index = @()
Assert-NoLinks $output
New-Item -ItemType Directory -Force -Path $output | Out-Null
foreach ($assembly in $assemblies) {
    $source = Split-Path $assembly -Parent
    $manifestPath = Join-Path $source 'plugin.json'
    if (-not (Test-Path -LiteralPath $manifestPath)) { continue } # Core / 纯后台组件不是桌面入口。
    Assert-NoLinks $source
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.id -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)+$' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') {
        throw "插件标识或版本无效：$manifestPath"
    }
    $relative = "$($manifest.id)/$($manifest.version)"
    if ($index | Where-Object Directory -eq $relative) { throw "重复桌面插件：$relative" }
    $package = [IO.Path]::GetFullPath((Join-Path $output $relative))
    # 只同步清单标识下的开发包，不触碰用户数据；路径及链接已检查。
    $boundary = $output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $package.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw '插件输出路径越界。' }
    New-Item -ItemType Directory -Force -Path $package | Out-Null
    # 双入口包先复制后台依赖，再复制桌面输出，与现有开发发布流程一致。
    $sources = @()
    if ($manifest.PSObject.Properties.Name -contains 'backgroundEntry') {
        $background = @($assemblies | Where-Object { [IO.Path]::GetFileName($_) -eq $manifest.backgroundEntry.assembly })
        if ($background.Count -ne 1) { throw "无法定位后台入口：$($manifest.backgroundEntry.assembly)" }
        $sources += Split-Path $background[0] -Parent
    }
    $sources += $source
    $files = @{}
    foreach ($directory in $sources) {
        Assert-NoLinks $directory
        foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File -Force) {
            $name = $file.FullName.Substring($directory.TrimEnd('\').Length + 1).Replace('\', '/')
            # 与桌面开发发布包一致，仅保留 Windows x64 原生资源。
            if ($name.StartsWith('runtimes/') -and -not $name.StartsWith('runtimes/win-x64/')) { continue }
            $files[$name] = $file
        }
    }
    foreach ($name in $files.Keys) {
        $file = $files[$name]
        $destination = Join-Path $package $name
        # 文件相同则不重写，F5 不必每次复制全部原生库；最终仍逐文件建立哈希索引。
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            if ((Get-Sha256 $destination) -eq (Get-Sha256 $file.FullName)) { continue }
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
    # 清理已不属于当前构建的文件，避免旧依赖被重新纳入可信索引。
    foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File -Force) {
        $name = $file.FullName.Substring($package.Length + 1).Replace('\', '/')
        if (-not $files.ContainsKey($name)) { Remove-Item -LiteralPath $file.FullName -Force }
    }
    $hashes = [ordered]@{}
    Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName | ForEach-Object {
        $name = $_.FullName.Substring($package.Length + 1).Replace('\', '/')
        $hashes[$name] = Get-Sha256 $_.FullName
    }
    $index += @{ Directory = $relative; Sha256 = $hashes }
}
if ($index.Count -eq 0) { throw '没有找到可加载的桌面插件清单。' }
# 开发索引只在所有文件复制成功后更新；发行包仍使用独立的发布脚本。
$indexPath = Join-Path $output 'trusted-plugins.json'
ConvertTo-Json -InputObject @($index) -Depth 8 | Set-Content -LiteralPath "$indexPath.tmp" -Encoding utf8
Move-Item -LiteralPath "$indexPath.tmp" -Destination $indexPath -Force
Write-Output "已自动同步 $($index.Count) 个桌面开发插件及可信索引：$output"
