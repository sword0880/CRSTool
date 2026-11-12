using System.Security.AccessControl;
using System.Security.Principal;

namespace CRS.AI.Infrastructure;

internal static class AuditArchiveAccess
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);

    public static void PrepareDirectory(string path)
    {
        AuditFileRules.RejectReparsePoints(path, allowMissing: true);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            var acl = new DirectorySecurity();
            acl.SetOwner(Administrators);
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { Administrators, SystemAccount })
                acl.AddAccessRule(new(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            directory.Create(acl);
        }
        var security = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("归档目录必须使用独立访问权限。");
        Validate(security);
    }

    public static void SetArchiveOwner(string path)
    {
        var file = new FileInfo(path);
        var acl = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        acl.SetOwner(Administrators);
        file.SetAccessControl(acl);
        Validate(acl);
    }

    public static void ValidateFile(string path)
    {
        AuditFileRules.RejectReparsePoints(path);
        Validate(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    }

    private static void Validate(FileSystemSecurity security)
    {
        static bool Trusted(IdentityReference? identity) => identity == Administrators || identity == SystemAccount;
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))) ||
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow && !Trusted(rule.IdentityReference)))
            throw new UnauthorizedAccessException("归档仅允许管理员及系统账户访问。");
    }
}
