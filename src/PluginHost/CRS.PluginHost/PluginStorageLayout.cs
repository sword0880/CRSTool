using System.IO;
using CRS.Plugin.Abstractions;

namespace CRS.PluginHost;

/// <summary>版本指针和恢复日志留在固定目录，候选数据库放在独立代次目录。</summary>
public sealed class PluginStorageLayout
{
    public string Home { get; }
    public string ScheduledTasksDirectory {get;}
    public string DataRoot=>Path.Combine(Home,"data");
    public string Raw=>Path.Combine(Home,"raw");
    public string Backup=>Path.Combine(Home,"backup");
    public PluginStorageLayout(string storageRoot,string id) {Home=PackageVerifier.Resolve(storageRoot,id);ScheduledTasksDirectory=PackageVerifier.Resolve(storageRoot,"scheduled-tasks");}
    public string DataDirectory(string generation)
    {
        // legacy仅表示插件自己的既有market.db，绝不探测或迁移税务旧目录。
        if(generation=="legacy")return DataRoot;
        if(string.IsNullOrWhiteSpace(generation) || generation.Length>64 || !generation.All(x=>char.IsAsciiLetterOrDigit(x) || x=='-'))throw new InvalidDataException("数据库代次标识无效。");
        return PackageVerifier.Resolve(Path.Combine(DataRoot,"generations"),generation);
    }
    public string Database(string generation)=>Path.Combine(DataDirectory(generation),"market.db");
    public UpgradeCoordinator Coordinator=>new(DataRoot);
    public IDisposable AcquireLease()
    {
        Directory.CreateDirectory(DataRoot);
        // 文件锁覆盖整个会话或离线升级，阻止另一进程同时进行迁移和事实写入。
        return new FileStream(Path.Combine(DataRoot,"plugin-session.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
    }
    public IPluginWriteGuardedPaths Paths(string packageVersion,string generation,CancellationToken lifetime)=>new GuardedPaths(this,packageVersion,generation,lifetime);

    private sealed class GuardedPaths: IPluginWriteGuardedPaths
    {
        private readonly PluginStorageLayout layout;
        private readonly ActiveGeneration expected;
        private readonly CancellationToken lifetime;
        public string Data {get;}
        public string Raw=>layout.Raw;
        public string Log=>Path.Combine(layout.Home,"log");
        public string Backup=>layout.Backup;
        // 主程序任务元数据也属于私有运行文件，证据导出不能覆盖它。
        public IReadOnlyList<string> ProtectedDirectories=>[layout.Home,layout.ScheduledTasksDirectory];
        public GuardedPaths(PluginStorageLayout layout,string version,string generation,CancellationToken lifetime)
        {
            this.layout=layout;this.lifetime=lifetime;expected=new(version,generation);Data=layout.DataDirectory(generation);
            foreach(var directory in new[]{Data,Raw,Log,Backup})Directory.CreateDirectory(directory);
        }
        public void BeforeWrite()
        {
            lifetime.ThrowIfCancellationRequested();
            var coordinator=layout.Coordinator;var journal=coordinator.Read();var active=coordinator.Active();
            if(active is not null && active!=expected)throw new InvalidDataException("当前会话的数据版本已失效，停止写入。");
            if(journal is null)return;
            if(journal.Stage==UpgradeStage.Switched)
            {
                if(journal.Candidate!=expected)throw new InvalidDataException("候选版本与写入会话不一致。");
                // 在第一次新事实写入前持久标记，失败恢复不得抹去这次写入。
                if(!journal.NewFactsWritten)coordinator.MarkBeforeNewFactWrite();
            }
            else if(journal.Stage is not (UpgradeStage.Committed or UpgradeStage.RolledBack))throw new InvalidDataException("插件处于升级恢复状态，禁止写入。");
        }
    }
}
