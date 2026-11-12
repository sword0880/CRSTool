using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Infrastructure;
using CRS.AI.Query;
using CRS.Worker.Ipc;

namespace CRS.Tests.AiQuery.Acceptance;

internal static class AuditArchiveAcceptance
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CRS-AuditArchive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var active = Path.Combine(root, "events.jsonl");
        var lease = Path.Combine(root, "audit-maintenance.lock");
        var archive = Path.Combine(root, "Archive");
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var gateway = new SecurityIdentifier(AiGatewayWorkerEndpoint.ServiceSid);
        await File.WriteAllTextAsync(active, "");
        var acl = new FileSecurity();
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(system, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new(gateway, FileSystemRights.Read | FileSystemRights.Write, AccessControlType.Allow));
        new FileInfo(active).SetAccessControl(acl);
        try
        {
            var entry = new AiReadAuditEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, new string('A', 64),
                AiCatalog.Overview, "Attempt", "Received", new string('B', 64));
            var sink = new AiAppendOnlyFileAuditSink(active, lease, maxFileBytes: 4096);
            using (AiAuditMaintenanceLease.OpenService(lease))
            {
                await sink.WriteAsync(entry, default);
                Check(Rejects(() => AiAuditArchive.Rotate(active, lease)), "服务运行时互斥锁拒绝归档");
            }
            var bytes = await File.ReadAllBytesAsync(active);
            var sourceHash = Convert.ToHexString(SHA256.HashData(bytes));

            using (File.Open(active, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                Check(Rejects(() => AiAuditArchive.Rotate(active, lease)), "尚未关闭的写入句柄会阻止切换");

            var receipt = AiAuditArchive.Rotate(active, lease);
            Check(receipt is { Events: 1 } && receipt.Sha256 == sourceHash && receipt.Bytes == bytes.Length &&
                  new FileInfo(active).Length == 0, "复制核验成功后才切换为空的活动日志");
            var archived = Path.Combine(archive, receipt!.ArchiveFile);
            Check(File.ReadAllBytes(archived).SequenceEqual(bytes) && !HasRule(archived, gateway),
                "归档字节保持一致，Gateway 服务账户没有访问规则");
            Check(HasRule(active, gateway) && AiAuditArchive.Rotate(active, lease) is null,
                "切换保留活动文件权限，空日志重复运行无副作用");
            using (AiAuditMaintenanceLease.OpenService(lease))
                await sink.WriteAsync(entry, default);
            Check(AiAuditArchive.Rotate(active, lease) == receipt &&
                  Directory.EnumerateFiles(archive, "*.jsonl").Count() == 1, "相同指纹重试复用已验证的归档");

            var corrupt = "{}\n";
            await File.WriteAllTextAsync(active, corrupt);
            Check(Rejects(() => AiAuditArchive.Rotate(active, lease)) && await File.ReadAllTextAsync(active) == corrupt,
                "畸形记录保留原文件");
            await File.WriteAllBytesAsync(active, bytes);
            await File.WriteAllTextAsync(archived, "损坏的归档\n");
            Check(Rejects(() => AiAuditArchive.Rotate(active, lease)) && File.ReadAllBytes(active).SequenceEqual(bytes),
                "已有同名归档损坏时保留活动日志");

            await File.WriteAllTextAsync(active, "");
            var written = 0;
            var reachedLimit = false;
            using (AiAuditMaintenanceLease.OpenService(lease))
            {
                for (; written < 32; written++)
                {
                    try { await sink.WriteAsync(entry with { RequestId = Guid.NewGuid() }, default); }
                    catch (IOException) { reachedLimit = true; break; }
                }
            }
            var full = AiAuditArchive.Rotate(active, lease);
            using (AiAuditMaintenanceLease.OpenService(lease))
                await sink.WriteAsync(entry, default);
            Check(reachedLimit && written > 1 && full?.Events == written && new FileInfo(active).Length > 0,
                "达到容量上限后完整归档，重新启动写入可用");
        }
        finally
        {
            if (Directory.Exists(archive))
            {
                foreach (var file in Directory.EnumerateFiles(archive)) File.Delete(file);
                Directory.Delete(archive);
            }
            File.Delete(active);
            File.Delete(lease);
            Directory.Delete(root);
        }
    }

    private static bool HasRule(string path, SecurityIdentifier sid) =>
        new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().Any(rule => rule.IdentityReference == sid);

    private static bool Rejects(Func<AiAuditArchiveReceipt?> rotate)
    {
        try { _ = rotate(); return false; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { return true; }
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS 审计归档 " + label);
    }
}
