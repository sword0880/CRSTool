using System.Security.Cryptography;
using CRS.IO;
using CRS.Worker.Abstractions;
using CRS.Worker.PluginHost;

namespace CRS.Worker.Service;

/// <summary>服务维护组合根：绑定发行、代次、快照及私有派生密钥，不引用任何插件业务程序集。</summary>
public static class WorkerStorageMigrations
{
    private sealed record Item(string PluginId,string MigrationId,string PackageHash,BackgroundStorageMigrationResult Result);
    private sealed record Receipt(int Version,string OperationId,string ReleaseHash,string SnapshotManifestHash,IReadOnlyList<Item> Items);
    public static async Task RunAsync(string program,string data,string plugins,string trust,WorkerUpgradeJournal journal,byte[] masterKey,CancellationToken token)
    {
        var record=journal.Read();if(record is null||record.Stage is "Committed" or "RolledBack")return;
        if(record.Stage!="Starting"||!record.NewFactsPossible||record.BackupManifestHash is null||!WorkerUpgradeJournal.ReleaseHash(program).Equals(record.CandidateReleaseHash,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("存储维护缺少明确的候选代次。");
        var plans=WorkerStorageMigrationPlans.Read(program,true,token);if(plans.Length==0)return;
        if(!Path.GetFullPath(plugins).Equals(Path.Combine(Path.GetFullPath(program),"Plugins"),StringComparison.OrdinalIgnoreCase)
            ||!Path.GetFullPath(trust).Equals(Path.Combine(Path.GetFullPath(program),"trusted-background-plugins.json"),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("维护插件和信任索引必须属于本次发行。");
        var path=ReceiptPath(data,record.Id);var purpose="CRS.Worker.StorageMigrations/v1/"+record.Id;
        var auditKey=HKDF.DeriveKey(HashAlgorithmName.SHA256,masterKey,32,info:"CRS.Worker.StorageMigrations.Audit/v1"u8.ToArray());
        try
        {
            if(File.Exists(path))
            {
                var saved=AuthenticatedJsonFile.Read<Receipt>(path,auditKey,purpose);
                if(saved.Version!=1||saved.OperationId!=record.Id||saved.ReleaseHash!=record.CandidateReleaseHash||saved.SnapshotManifestHash!=record.BackupManifestHash
                    ||saved.Items.Count!=plans.Length||saved.Items.Where((x,i)=>x.PluginId!=plans[i].PluginId||x.MigrationId!=plans[i].MigrationId).Any())throw new InvalidDataException("维护回执身份与本次计划不一致。");
            }
            var results=new List<Item>();
            foreach(var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                var package=await BackgroundPluginCatalog.VerifyStorageMigrationPackageAsync(plugins,trust,plan.PluginId,token).ConfigureAwait(false);
                var context=new WorkerPluginContext(plan.PluginId,data,masterKey);var key=context.OpenDataKey();
                try
                {
                    var live=PhysicalPathGuard.Child(data,"plugins/"+plan.PluginId);
                    var snapshot=PhysicalPathGuard.Child(data,"maintenance/"+record.Id+"/snapshot/plugins/"+plan.PluginId);
                    var request=new BackgroundStorageMigrationContext(plan.PluginId,plan.MigrationId,Guid.ParseExact(record.Id,"N"),live,snapshot,key);
                    var result=await IsolatedStorageMigration.RunAsync(PhysicalPathGuard.Child(program,"Runner/CRS.Worker.PluginRunner.exe"),plugins,trust,package,
                        request,TimeSpan.FromSeconds(plan.TimeoutSeconds),token).ConfigureAwait(false);
                    results.Add(new(plan.PluginId,plan.MigrationId,RunnerChannel.PackageHash(package),result));
                }
                finally{CryptographicOperations.ZeroMemory(key);}
            }
            AuthenticatedJsonFile.Write(path,auditKey,purpose,new Receipt(1,record.Id,record.CandidateReleaseHash,record.BackupManifestHash,results));
            journal.MarkStorageMigrationsReady(program,FileHash(path));
        }
        finally{CryptographicOperations.ZeroMemory(auditKey);}
    }
    internal static string ReceiptPath(string data,string id)=>PhysicalPathGuard.Child(data,"maintenance/"+id+"/storage-migrations.json");
    internal static string FileHash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
}
