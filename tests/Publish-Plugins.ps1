param([string]$OutputDirectory = "$PSScriptRoot/../artifacts/plugin-preview", [switch]$IncludeDemo, [ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$destination = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $destination | Out-Null
dotnet publish "$repo/src/CRS.Desktop.Wpf/CRS.Desktop.Wpf.csproj" -c $Configuration --no-restore -m:1 -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Host publish failed' }
$definitions = @(@{Id='crs.national-team';Project='CRS.Plugins.NationalTeamWatch'})
if ($IncludeDemo) { $definitions += @{Id='crs.demo';Project='CRS.Plugins.Demo'} }
$index = @()
foreach ($definition in $definitions) {
    $relative = "$($definition.Id)/1.0.0"
    $package = Join-Path $destination "Plugins/$relative"
    if (Test-Path -LiteralPath $package) { throw "Use a new output directory; immutable package already exists: $package" }
    dotnet publish "$repo/src/Plugins/$($definition.Project)/$($definition.Project).csproj" -c $Configuration --no-restore -m:1 -o $package
    if ($LASTEXITCODE -ne 0) { throw 'Plugin publish failed' }
    # 预览针对Windows x64；生成可信文件索引前删除其他平台的原生资源。
    # 保留可移植依赖描述，运行时只解析当前平台需要的原生文件。
    $runtimeDirectory = Join-Path $package 'runtimes'
    if (Test-Path -LiteralPath $runtimeDirectory) {
        Get-ChildItem -LiteralPath $runtimeDirectory -Directory | Where-Object Name -ne 'win-x64' | ForEach-Object {
            $targetPath = [IO.Path]::GetFullPath($_.FullName)
            $packageBoundary = [IO.Path]::GetFullPath($package).TrimEnd('\') + '\'
            if (-not $targetPath.StartsWith($packageBoundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime prune path outside package' }
            if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point in publish output' }
            Remove-Item -LiteralPath $targetPath -Recurse -Force
        }
    }
    $hashes = [ordered]@{}
    Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object {
        $name = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\','/')
        $hashes[$name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $index += @{Directory=$relative;Sha256=$hashes}
}
# 此脚本只用于开发包装；生产必须把宿主所有的索引绑定到经过验证的发行链。
ConvertTo-Json -InputObject @($index) -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'Plugins/trusted-plugins.json') -Encoding utf8
Write-Output "Offline preview package: $destination"
