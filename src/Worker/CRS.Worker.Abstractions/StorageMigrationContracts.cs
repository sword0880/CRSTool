namespace CRS.Worker.Abstractions;

/// <summary>仅由显式维护代次调用的独立入口；不创建普通插件会话或定时任务。</summary>
public interface IBackgroundStorageMigrationEntry
{
    Task<BackgroundStorageMigrationResult> MigrateAsync(BackgroundStorageMigrationContext context,CancellationToken token);
}

/// <summary>路径由宿主绑定，密钥仅为该插件的派生副本；入口不得保留密钥。</summary>
public sealed record BackgroundStorageMigrationContext(string PluginId,string MigrationId,Guid OperationId,
    string DataDirectory,string SnapshotDirectory,byte[] DataKey);
public sealed record BackgroundStorageMigrationResult(string MigrationId,Guid OperationId,string State,
    int ConvertedDatabases,string EvidenceSha256);
