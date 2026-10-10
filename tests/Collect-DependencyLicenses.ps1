param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/acceptance/licenses'))
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$inventory = @{}
# 按生产项目实际还原结果收集所有传递依赖，不能只检查集中声明的直接引用。
foreach ($project in @('CRS.Domain','CRS.Application','CRS.Security','CRS.Infrastructure','CRS.Desktop.Wpf')) {
    $assets = Get-Content -LiteralPath (Join-Path $repository "src/$project/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    foreach ($entry in $assets.libraries.GetEnumerator()) {
        if ($entry.Value.type -ne 'package' -or $inventory.ContainsKey($entry.Key)) { continue }
        $packageDirectory = $null
        foreach ($cache in $assets.packageFolders.Keys) {
            $candidate = Join-Path $cache $entry.Value.path
            if (Test-Path -LiteralPath $candidate) { $packageDirectory = $candidate; break }
        }
        if (-not $packageDirectory) { throw "找不到已还原依赖：$($entry.Key)" }
        $nuspec = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' | Select-Object -First 1
        [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
        $license = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        $url = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
        $name, $version = $entry.Key.Split('/')
        $destination = Join-Path $output "$name-$version"
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        Copy-Item -LiteralPath $nuspec.FullName -Destination $destination
        $notices = @(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE|THIRD[-_ ]?PARTY)' })
        $copies = @()
        foreach ($notice in $notices) {
            $relative = [IO.Path]::GetRelativePath($packageDirectory,$notice.FullName)
            $target = Join-Path $destination $relative
            New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $notice.FullName -Destination $target
            $copies += "$name-$version/$relative"
        }
        # 转为普通对象后再排序和导出，避免有序字典被 CSV 当作空属性对象。
        $inventory[$entry.Key] = [pscustomobject][ordered]@{
            Package=$name; Version=$version; LicenseType=$(if ($license) {$license.type} else {'unspecified'})
            License=$(if ($license) {$license.InnerText} else {''}); LicenseUrl=$(if ($url) {$url.InnerText} else {''})
            Notices=$copies; RestoreSha512=$entry.Value.sha512
            ReviewStatus=$(if ($license) {'metadata_collected_not_legally_approved'} else {'license_metadata_requires_review'})
        }
    }
}
$rows = @($inventory.Values | Sort-Object Package,Version)
$rows | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'inventory.json') -Encoding utf8
$rows | Select-Object Package,Version,LicenseType,License,LicenseUrl,ReviewStatus | Export-Csv -LiteralPath (Join-Path $output 'inventory.csv') -NoTypeInformation -Encoding utf8
Write-Output "已收集 $($rows.Count) 个生产依赖的许可证元数据及可用声明；尚未代表分发许可审核通过。"
