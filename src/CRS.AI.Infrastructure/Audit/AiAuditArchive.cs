using System.Security.AccessControl;
using System.Security.Cryptography;

namespace CRS.AI.Infrastructure;

public sealed record AiAuditArchiveReceipt(string ArchiveFile, long Bytes, int Events, string Sha256);

/// <summary>先验证并保存原日志，再以空文件替换活动日志。按内容指纹识别已完成的归档。</summary>
public static class AiAuditArchive
{
    public static AiAuditArchiveReceipt? Rotate(string auditPath, string maintenanceLockPath)
    {
        auditPath = AuditFileRules.LocalPath(auditPath);
        using var lease = AiAuditMaintenanceLease.OpenArchive(maintenanceLockPath);
        AuditFileRules.RejectReparsePoints(auditPath);
        var parent = Path.GetDirectoryName(auditPath)!;
        var archiveDirectory = Path.Combine(parent, "Archive");
        var staging = Path.Combine(archiveDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        var replacement = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long length;
            (int Events, string Sha256) validated;
            string archiveFile;
            using (var source = new FileStream(auditPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                length = source.Length;
                if (length == 0) return null;
                if (length > AuditFileRules.MaximumArchiveBytes)
                    throw new InvalidDataException("活动审计文件超过归档容量。");
                validated = AuditArchiveReader.Inspect(source);
                AuditArchiveAccess.PrepareDirectory(archiveDirectory);
                archiveFile = Path.Combine(archiveDirectory, validated.Sha256 + ".jsonl");
                // 若上次已保存归档但未切换，不再占用空间生成同样的临时副本。
                if (!File.Exists(archiveFile))
                    CopyToArchive(source, staging, archiveFile, length, validated.Sha256);
                AuditArchiveAccess.ValidateFile(archiveFile);
                VerifyFingerprint(archiveFile, length, validated.Sha256);
            }

            // 归档已经刷盘并核验；换页失败时保留原活动文件及已验证归档，下一次可安全重试。
            var active = new FileInfo(auditPath);
            var activeAcl = active.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            var replacementAcl = new FileSecurity();
            replacementAcl.SetSecurityDescriptorBinaryForm(activeAcl.GetSecurityDescriptorBinaryForm(),
                AccessControlSections.Owner | AccessControlSections.Access);
            using (var fresh = new FileStream(replacement, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fresh.Flush(flushToDisk: true);
            new FileInfo(replacement).SetAccessControl(replacementAcl);
            File.Move(replacement, auditPath, overwrite: true);
            return new(Path.GetFileName(archiveFile), length, validated.Events, validated.Sha256);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
            File.Delete(replacement);
        }
    }

    private static void CopyToArchive(Stream source, string staging, string archiveFile, long length, string hash)
    {
        source.Position = 0;
        using (var target = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        }
        AuditArchiveAccess.SetArchiveOwner(staging);
        VerifyFingerprint(staging, length, hash);
        File.Move(staging, archiveFile);
    }

    private static void VerifyFingerprint(string path, long length, string expected)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != length || Convert.ToHexString(SHA256.HashData(file)) != expected)
            throw new InvalidDataException("归档副本指纹核验失败。");
    }
}
