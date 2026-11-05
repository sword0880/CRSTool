namespace CRS.Maintenance;

/// <summary>资料清理选项不包含客户端路径；实际允许范围和保护策略由数据所有者决定。</summary>
public sealed record DataCleanupOptions(int LogRetentionDays=14,int HistoricalBackupsToKeep=2,bool IncludeHistoricalBackups=false);
public sealed record DataCleanupEntry(string Id,string Category,string Name,string RelativePath,long? Bytes,int? Files,bool Eligible,bool Selected,string Reason);
/// <summary>扫描总量未确定时只报告实际已处理数量；校验字节包含对同一文件的再次核验。</summary>
public sealed record DataCleanupProgress(string Phase,string? CurrentItem,long ScannedFiles=0,long ScannedBytes=0,long VerifiedFiles=0,long VerifiedBytes=0);
public sealed record DataCleanupStatus(Guid OperationId,string State,DateTimeOffset UpdatedAt,DateTimeOffset? ExpiresAt,
    IReadOnlyList<DataCleanupEntry> Entries,long DeletedBytes,int DeletedFiles,string Message,bool CanExecute=false,int FormatVersion=1,bool CountsComplete=true)
{
    public DataCleanupProgress? Progress{get;init;}
}
