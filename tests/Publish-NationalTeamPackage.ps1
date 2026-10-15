param([Parameter(Mandatory)][string]$OutputDirectory,[ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'ETF双入口包目录必须不存在。'}
# 两个宿主使用同一个完整包和同一清单；只执行各自入口，绝不由服务实例化WPF。
& dotnet publish (Join-Path $repo 'src/Plugins/CRS.Plugins.NationalTeam.Jobs') -c $Configuration --no-self-contained --no-restore -m:1 -o $output
if($LASTEXITCODE -ne 0){throw 'ETF后台入口发布失败。'}
& dotnet publish (Join-Path $repo 'src/Plugins/CRS.Plugins.NationalTeamWatch') -c $Configuration --no-self-contained --no-restore -m:1 -o $output
if($LASTEXITCODE -ne 0){throw 'ETF页面入口发布失败。'}
# 仅保留本次Windows x64验收需要的原生库，再由外层发行脚本对完整包建立可信索引。
$runtimes=Join-Path $output 'runtimes'
if(Test-Path -LiteralPath $runtimes){
    foreach($folder in Get-ChildItem -LiteralPath $runtimes -Directory){
        if($folder.Name -eq 'win-x64'){continue}
        $resolved=[IO.Path]::GetFullPath($folder.FullName)
        if(-not $resolved.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $folder.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '运行库清理路径不安全。'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
