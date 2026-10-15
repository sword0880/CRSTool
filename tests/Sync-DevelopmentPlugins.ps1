param([Parameter(Mandatory)][string]$PackageDirectory,[ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$source=[IO.Path]::GetFullPath((Join-Path $PackageDirectory 'Plugins'))
if(-not (Test-Path -LiteralPath (Join-Path $source 'trusted-plugins.json'))){throw '必须提供已生成可信索引的桌面开发包。'}
$boundary=[IO.Path]::GetFullPath((Join-Path $repo 'src/CRS.Desktop.Wpf/bin')).TrimEnd('\')+'\'
$target=[IO.Path]::GetFullPath((Join-Path $boundary "$Configuration/net10.0-windows10.0.19041.0/Plugins"))
# 只替换工作区内已知的开发构建插件目录；不读取或删除用户研究库。
if(-not $target.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $target -Leaf) -ne 'Plugins'){throw '开发同步目标越界。'}
foreach($root in @($source,$target)){
    if(Test-Path -LiteralPath $root){
        $items=@(Get-Item -LiteralPath $root)+@(Get-ChildItem -LiteralPath $root -Recurse -Force)
        if($items | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw '开发插件同步路径包含链接。'}
    }
}
if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
Copy-Item -LiteralPath $source -Destination $target -Recurse
Write-Output "已同步 $Configuration 插件包，F5启动会加载当前Worker只读页面。"
