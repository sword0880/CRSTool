param([Parameter(Mandatory)][string]$PreviousDirectory,[Parameter(Mandatory)][string]$CandidateDirectory,
    [string]$OutputPath='artifacts/pipe-eof-binary-api.json')
$ErrorActionPreference='Stop'
$contexts=@();$apis=@()
try{
    foreach($root in @($PreviousDirectory,$CandidateDirectory)){
        $context=[Runtime.Loader.AssemblyLoadContext]::new(('CRS.ApiAudit.'+[Guid]::NewGuid().ToString('N')),$true)
        $contexts+=,$context;$types=@{}
        foreach($name in @('CRS.Worker.Abstractions.dll','CRS.Worker.Ipc.dll')){
            $assembly=$context.LoadFromAssemblyPath([IO.Path]::GetFullPath((Join-Path $root $name)))
            foreach($type in $assembly.GetExportedTypes()){
                $methods=@($type.GetMethods([Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly'))
                $members=@($type.GetConstructors() | ForEach-Object {$_.ToString()})+@($methods | ForEach-Object {$_.ToString()})
                $constants=@{};foreach($field in $type.GetFields([Reflection.BindingFlags]'Public,Static,DeclaredOnly') | Where-Object IsLiteral){$constants[$field.Name]=$field.GetRawConstantValue()}
                $types[$type.FullName]=@{Members=$members;Abstract=@($methods | Where-Object IsAbstract | ForEach-Object {$_.ToString()});Constants=$constants;
                    Kind=($type.IsValueType.ToString()+'/'+$type.IsEnum+'/'+$type.IsInterface+'/'+$type.BaseType.FullName)}
            }
        }
        $apis+=,$types
    }
    $failures=@()
    foreach($name in $apis[0].Keys){
        $old=$apis[0][$name];$current=$apis[1][$name]
        if(-not $current){$failures+=$name+': removed type';continue}
        if($old.Kind -ne $current.Kind){$failures+=$name+': changed kind/base'}
        foreach($member in $old.Members){if($member -notin $current.Members){$failures+=$name+' :: removed '+$member}}
        foreach($member in $current.Abstract){if($member -notin $old.Abstract){$failures+=$name+' :: new abstract '+$member}}
        foreach($constant in $old.Constants.Keys){if(-not $current.Constants.ContainsKey($constant) -or $current.Constants[$constant] -ne $old.Constants[$constant]){$failures+=$name+' :: changed '+$constant}}
    }
    @{Previous=[IO.Path]::GetFullPath($PreviousDirectory);Candidate=[IO.Path]::GetFullPath($CandidateDirectory);PreviousTypes=$apis[0].Count;Failures=$failures} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath
    if($failures.Count -gt 0){throw ($failures -join [Environment]::NewLine)}
    Write-Output "PASS 已安装Worker契约和IPC的 $($apis[0].Count) 个公开类型：原构造器/方法/常量保留，无新增抽象实现要求。"
}finally{foreach($context in $contexts){$context.Unload()}}
