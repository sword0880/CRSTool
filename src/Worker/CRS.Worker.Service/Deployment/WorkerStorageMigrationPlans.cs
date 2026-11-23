using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CRS.IO;
using CRS.Serialization;

namespace CRS.Worker.Service;

public sealed record WorkerStorageMigrationPlan([property:JsonPropertyName("pluginId")]string PluginId,
    [property:JsonPropertyName("migrationId")]string MigrationId,[property:JsonPropertyName("timeoutSeconds")]int TimeoutSeconds);

/// <summary>发行内显式计划；只有未完成的维护代次读取它，不由普通启动推断或自动转换资料。</summary>
public static class WorkerStorageMigrationPlans
{
    public static WorkerStorageMigrationPlan[] Read(string programDirectory,bool verifyFiles=false,CancellationToken token=default)
    {
        var path=PhysicalPathGuard.Child(programDirectory,"release.json");
        if(new FileInfo(path).Length>2*1024*1024)throw new InvalidDataException("发行清单超限。");
        var release=StrictJson.Read<Dictionary<string,JsonElement>>(File.ReadAllText(path),2*1024*1024);
        if(!release.TryGetValue("storageMigrations",out var value))return [];
        var plans=StrictJson.Read<WorkerStorageMigrationPlan[]>(value.GetRawText(),16384);
        bool Valid(string id)=>Regex.IsMatch(id,"^[a-z0-9][a-z0-9.-]{0,79}$",RegexOptions.CultureInvariant);
        if(plans.Length>4||plans.Any(x=>x is null||!Valid(x.PluginId)||!Valid(x.MigrationId)||x.TimeoutSeconds is <1 or >600)
            ||plans.Select(x=>x.PluginId).Distinct(StringComparer.Ordinal).Count()!=plans.Length||plans.Sum(x=>x.TimeoutSeconds)>1200)throw new InvalidDataException("存储维护计划无效或超限。");
        if(verifyFiles&&plans.Length>0)VerifyFiles(programDirectory,release,token);
        return plans;
    }
    private static void VerifyFiles(string root,Dictionary<string,JsonElement> release,CancellationToken token)
    {
        if(!release.TryGetValue("files",out var value))throw new InvalidDataException("维护发行没有文件索引。");
        var files=StrictJson.Read<Dictionary<string,string>>(value.GetRawText(),2*1024*1024);
        if(files.Count is <1 or >10000||!files.ContainsKey("trusted-background-plugins.json")||!files.ContainsKey("Runner/CRS.Worker.PluginRunner.exe")
            ||!files.ContainsKey("CRS.Worker.Service.exe"))throw new InvalidDataException("维护发行文件索引不完整。");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var (relative,expected) in files)
        {
            token.ThrowIfCancellationRequested();var path=PhysicalPathGuard.Child(root,relative);
            if(relative is "release.json" or "release.json.p7s"||!seen.Add(path)||!CRS.Common.Integrity.ContentFingerprint.IsSha256Hex(expected))throw new InvalidDataException("维护发行文件条目无效。");
            using var stream=File.OpenRead(path);
            if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("维护发行文件已改变。");
        }
    }
}
