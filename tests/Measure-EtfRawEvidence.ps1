param([Parameter(Mandatory)][string]$RawDirectory)
$ErrorActionPreference='Stop'
# 只读测量公开研究原件，不读取数据库或密钥，不改变原件及质量状态。
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
public static class EtfRawReadProfile {
 public static object Run(string root) {
  var timer=Stopwatch.StartNew();var paths=Directory.GetFiles(root,"*.raw");
  long enumerate=timer.ElapsedMilliseconds,bytes=0;int valid=0,invalid=0;
  foreach(var path in paths) {
   var hash=Path.GetFileNameWithoutExtension(path);
   if(hash.Length!=64||!File.Exists(path)||new FileInfo(path).Length>8*1024*1024){invalid++;continue;}
   using(var stream=File.OpenRead(path)) {
    bytes+=stream.Length;
    if(Convert.ToHexString(SHA256.HashData(stream))==hash)valid++;else invalid++;
   }
  }
  return new { Files=paths.Length,Bytes=bytes,Valid=valid,Invalid=invalid,EnumerateMs=enumerate,TotalMs=timer.ElapsedMilliseconds };
 }
}
'@
[EtfRawReadProfile]::Run([IO.Path]::GetFullPath($RawDirectory)) | ConvertTo-Json -Compress
