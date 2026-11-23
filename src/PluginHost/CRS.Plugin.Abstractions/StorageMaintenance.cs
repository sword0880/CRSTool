namespace CRS.Plugin.Abstractions;

/// <summary>可选离线存储维护入口；不创建界面或注册后台任务。</summary>
public interface IPluginStorageMaintenanceEntry
{
    IPluginStorageMaintenance CreateStorageMaintenance(IPluginDataPaths paths);
}

/// <summary>由插件处理自身数据库快照、候选迁移和证据引用，宿主不认识业务表。</summary>
public interface IPluginStorageMaintenance
{
    int SupportedSchemaVersion { get; }
    Task SnapshotAsync(string sourceDatabase,string destinationDatabase,CancellationToken cancellationToken);
    Task MigrateAsync(string candidateDatabase,int targetSchemaVersion,CancellationToken cancellationToken);
    Task VerifyAsync(string database,int expectedSchemaVersion,CancellationToken cancellationToken);
}

/// <summary>宿主可选写入门槛；可信插件在写入研究事实和原件前调用，不构成进程沙箱。</summary>
public interface IPluginWriteGuardedPaths : IPluginDataPaths
{
    IReadOnlyList<string> ProtectedDirectories { get; }
    void BeforeWrite();
}
