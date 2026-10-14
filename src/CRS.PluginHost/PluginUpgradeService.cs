using System.IO;
using System.Text.Json;
using CRS.Plugin.Abstractions;

namespace CRS.PluginHost;

/// <summary>仅维护已绑定到宿主发行清单的版本；必须在独立升级进程、插件未运行时调用。</summary>
public sealed class PluginUpgradeService(string releaseRoot,string storageRoot,Action<string>? checkpoint=null)
{
    public async Task PrepareAsync(string pluginId,string candidateVersion,CancellationToken cancellationToken=default)
    {
        var packages=JsonSerializer.Deserialize<TrustedPackage[]>(await File.ReadAllTextAsync(Path.Combine(releaseRoot,"trusted-plugins.json"),cancellationToken),PackageVerifier.Json)??[];
        var verified=await Task.Run(()=>packages.Select(p=>(Package:p,Manifest:PackageVerifier.Verify(releaseRoot,p,new(1,0,0)))).Where(x=>x.Manifest.Id==pluginId).ToList(),cancellationToken);
        var candidate=verified.SingleOrDefault(x=>x.Manifest.Version==candidateVersion);
        if(candidate.Manifest is null)throw new InvalidDataException("候选版本不在可信发行清单中。");
        var layout=new PluginStorageLayout(storageRoot,pluginId);using var lease=layout.AcquireLease();var coordinator=new UpgradeCoordinator(layout.DataRoot,checkpoint);
        var active=coordinator.Active();
        var alternatives=verified.Where(x=>x.Manifest.Version!=candidateVersion).ToList();
        if(active is null && alternatives.Count!=1)throw new InvalidDataException("未明确选择既有版本，拒绝猜测升级起点。");
        var previous=active??new ActiveGeneration(alternatives[0].Manifest.Version,"legacy");
        if(Version.Parse(previous.PackageVersion)>=Version.Parse(candidateVersion))throw new InvalidDataException("候选版本必须高于既有版本。");
        if(!verified.Any(x=>x.Manifest.Version==previous.PackageVersion))throw new InvalidDataException("旧版本不在可信发行清单中，不能准备可回退升级。");
        if(!File.Exists(layout.Database(previous.DbGeneration)))throw new InvalidDataException("既有插件数据库不存在。");
        using var loaded=new VerifiedPluginLoad(releaseRoot,candidate.Package,candidate.Manifest);
        if(loaded.Entry is not IPluginStorageMaintenanceEntry entry)throw new InvalidDataException("插件未提供离线存储维护契约。");
        var maintenance=entry.CreateStorageMaintenance(layout.Paths(candidateVersion,"legacy",cancellationToken));
        if(maintenance.SupportedSchemaVersion!=candidate.Manifest.SchemaVersion)throw new InvalidDataException("候选存储契约与清单Schema不匹配。");
        var next=new ActiveGeneration(candidateVersion,Guid.NewGuid().ToString("N"));
        var adapter=new MaintenanceParticipant(layout,maintenance,candidate.Manifest.SchemaVersion);
        await coordinator.PrepareAsync(previous,next,adapter,cancellationToken);
    }

    /// <summary>候选和快照使用不同路径，旧库始终不被迁移或覆盖。</summary>
    internal sealed class MaintenanceParticipant(PluginStorageLayout layout,IPluginStorageMaintenance maintenance,int schema):IPluginUpgradeParticipant
    {
        public Task SnapshotAsync(ActiveGeneration previous,CancellationToken cancellationToken)
        {
            var journal=layout.Coordinator.Read()??throw new InvalidDataException("升级日志缺失。");
            Directory.CreateDirectory(layout.Backup);
            return maintenance.SnapshotAsync(layout.Database(previous.DbGeneration),Path.Combine(layout.Backup,journal.Id+".db"),cancellationToken);
        }
        public async Task MigrateCandidateAsync(ActiveGeneration candidate,CancellationToken cancellationToken)
        {
            var journal=layout.Coordinator.Read()??throw new InvalidDataException("升级日志缺失。");
            Directory.CreateDirectory(layout.DataDirectory(candidate.DbGeneration));
            await maintenance.SnapshotAsync(Path.Combine(layout.Backup,journal.Id+".db"),layout.Database(candidate.DbGeneration),cancellationToken);
            await maintenance.MigrateAsync(layout.Database(candidate.DbGeneration),schema,cancellationToken);
        }
        public Task VerifyCandidateAsync(ActiveGeneration candidate,CancellationToken cancellationToken)=>maintenance.VerifyAsync(layout.Database(candidate.DbGeneration),schema,cancellationToken);
    }
}
