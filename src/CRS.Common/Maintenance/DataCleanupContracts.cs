namespace CRS.Maintenance;

/// <summary>资料清理选项不包含客户端路径；实际允许范围和保护策略由数据所有者决定。</summary>
public sealed record DataCleanupOptions(int LogRetentionDays=14,int HistoricalBackupsToKeep=2,bool IncludeHistoricalBackups=false);
public sealed record DataCleanupEntry(string Id,string Category,string Name,string RelativePath,long? Bytes,int? Files,bool Eligible,bool Selected,string Reason);
public sealed record DataCleanupStatus(Guid OperationId,string State,DateTimeOffset UpdatedAt,DateTimeOffset? ExpiresAt,
    IReadOnlyList<DataCleanupEntry> Entries,long DeletedBytes,int DeletedFiles,string Message,bool CanExecute=false,int FormatVersion=1,bool CountsComplete=true);
