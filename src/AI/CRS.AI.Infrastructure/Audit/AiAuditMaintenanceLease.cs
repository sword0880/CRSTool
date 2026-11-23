namespace CRS.AI.Infrastructure;

/// <summary>协调 Gateway 服务、落盘中的写入和离线归档。</summary>
public static class AiAuditMaintenanceLease
{
    public static FileStream OpenService(string path) =>
        Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

    internal static FileStream OpenWriter(string path) =>
        Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    internal static FileStream OpenArchive(string path) =>
        Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        path = AuditFileRules.LocalPath(path);
        AuditFileRules.RejectReparsePoints(path, allowMissing: mode == FileMode.OpenOrCreate);
        return new FileStream(path, mode, access, share, bufferSize: 1);
    }
}
