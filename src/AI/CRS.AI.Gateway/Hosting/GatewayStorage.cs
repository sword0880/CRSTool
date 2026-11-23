using System.Security.AccessControl;
using System.Security.Principal;
using CRS.Worker.Ipc;

namespace CRS.AI.Gateway.Hosting;

internal sealed class GatewayStorage(string root)
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier GatewayAccount = new(AiGatewayWorkerEndpoint.ServiceSid);
    private const FileSystemRights NoAuditChange = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public string AuthorizationPath => Path.Combine(root, "Authorization", "subjects.json");
    public string AuditMaintenancePath => Path.Combine(root, "Authorization", "audit-maintenance.lock");
    public string AuditPath => Path.Combine(root, "Audit", "events.jsonl");

    public static GatewayStorage Installed() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CRSAiGateway"));

    public void Validate()
    {
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            RejectReparse(directory.FullName);
        ValidateDirectory(root, protect: true, NoAuditChange);
        ValidateDirectory(Path.GetDirectoryName(AuthorizationPath)!, protect: false, NoAuditChange & ~FileSystemRights.Delete);
        ValidateDirectory(Path.GetDirectoryName(AuditPath)!, protect: false, NoAuditChange);
        ValidateFile(AuthorizationPath, gatewayCanOwn: true, FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
        if (File.Exists(AuditMaintenancePath))
            ValidateFile(AuditMaintenancePath, gatewayCanOwn: true, FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
        ValidateFile(AuditPath, gatewayCanOwn: false, NoAuditChange);
    }

    private static void ValidateDirectory(string path, bool protect, FileSystemRights forbidden)
    {
        RejectReparse(path);
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (protect && !acl.AreAccessRulesProtected) throw new UnauthorizedAccessException("Gateway 根目录权限必须独立配置。");
        ValidateAcl(acl, gatewayCanOwn: false, forbidden);
    }

    private static void ValidateFile(string path, bool gatewayCanOwn, FileSystemRights forbidden)
    {
        RejectReparse(path);
        ValidateAcl(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner),
            gatewayCanOwn, forbidden);
    }

    private static void ValidateAcl(FileSystemSecurity acl, bool gatewayCanOwn, FileSystemRights forbidden)
    {
        var owner = (SecurityIdentifier?)acl.GetOwner(typeof(SecurityIdentifier));
        if (owner != Admins && owner != SystemAccount && (!gatewayCanOwn || owner != GatewayAccount))
            throw new UnauthorizedAccessException("Gateway 存储所有者不受信任。");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid == Admins || sid == SystemAccount) continue;
            if (sid != GatewayAccount || (rule.FileSystemRights & forbidden) != 0)
                throw new UnauthorizedAccessException("Gateway 存储权限超出允许范围。");
        }
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Gateway 存储不接受符号链接或目录联接。");
    }
}
