using System.Security.AccessControl;
using System.Security.Principal;
namespace CRS.Worker.Service;

/// <summary>机器 DPAPI 依赖文件 ACL；开发控制台也必须封闭密钥目录。</summary>
public static class StorageSecurity
{
    private const int MaximumEntries = 100000;
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdminSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>开发目录可初始化权限；正式目录必须由安装器建立，启动时只验证。</summary>
    public static void Prepare(string directory, bool console)
    {
        RejectLinks(directory);
        if (console)
        {
            Directory.CreateDirectory(directory);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            var owner = WindowsIdentity.GetCurrent().User!; security.SetOwner(owner);
            foreach (var sid in new[] { owner, SystemSid, AdminSid })
                security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
        Validate(directory, console);
    }

    /// <summary>检查整棵数据树，包含受保护子项；不替管理员自动修复漂移后的权限。</summary>
    public static void Validate(string directory, bool console)
    {
        RejectLinks(directory);
        if (!Directory.Exists(directory)) throw new StorageSecurityException();
        var identity = console ? WindowsIdentity.GetCurrent().User! :
            (SecurityIdentifier)new NTAccount("NT SERVICE", "CRSBackgroundService").Translate(typeof(SecurityIdentifier));
        var allowed = new HashSet<string>(StringComparer.Ordinal) { SystemSid.Value, AdminSid.Value, identity.Value };
        var owners = new HashSet<string>(allowed, StringComparer.Ordinal);
        // Windows 会把 LocalService 创建的文件记为其所有；不能将共享账户加入可读 ACE 白名单。
        if (!console) owners.Add(new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null).Value);
        var root = new DirectoryInfo(directory);
        var rootSecurity = root.GetAccessControl();
        if (!rootSecurity.AreAccessRulesProtected) throw new StorageSecurityException();
        // 正式数据根的所有者必须保留为管理员或 SYSTEM，不能由共享服务账户接管根权限。
        if (!console && rootSecurity.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier rootOwner &&
            !rootOwner.Equals(SystemSid) && !rootOwner.Equals(AdminSid)) throw new StorageSecurityException();
        ValidateAcl(rootSecurity, allowed, owners);
        RequireInheritedAccess(rootSecurity, SystemSid, FileSystemRights.FullControl);
        RequireInheritedAccess(rootSecurity, AdminSid, FileSystemRights.FullControl);
        RequireInheritedAccess(rootSecurity, identity, console ? FileSystemRights.FullControl : FileSystemRights.Modify);
        var pending = new Stack<DirectoryInfo>(); pending.Push(root);
        var visited = 0;
        while (pending.TryPop(out var parent))
            foreach (var entry in parent.EnumerateFileSystemInfos())
            {
                if (++visited > MaximumEntries || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new StorageSecurityException();
                if (entry is DirectoryInfo child)
                {
                    ValidateAcl(child.GetAccessControl(), allowed, owners); pending.Push(child);
                }
                else ValidateAcl(((FileInfo)entry).GetAccessControl(), allowed, owners);
            }
    }

    /// <summary>任何额外主体的允许 ACE 都可能泄露机器 DPAPI 密文；空 DACL 同样拒绝。</summary>
    private static void ValidateAcl(FileSystemSecurity security, HashSet<string> allowed, HashSet<string> owners)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owners.Contains(owner.Value))
            throw new StorageSecurityException();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        if (rules.Length == 0 || rules.Any(rule => rule.AccessControlType != AccessControlType.Allow ||
            !allowed.Contains(rule.IdentityReference.Value))) throw new StorageSecurityException();
    }

    /// <summary>根目录必须同时保留管理员维护权限及服务继承权限，避免检查通过后子文件失去保护。</summary>
    private static void RequireInheritedAccess(DirectorySecurity security, SecurityIdentifier sid, FileSystemRights required)
    {
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var rights = (FileSystemRights)0;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow &&
                (rule.InheritanceFlags & inheritance) == inheritance && rule.PropagationFlags == PropagationFlags.None)
                rights |= rule.FileSystemRights;
        if ((rights & required) != required) throw new StorageSecurityException();
    }

    /// <summary>检查目标和全部既有祖先；密钥读取也单独调用，拒绝文件链接绕过目录保护。</summary>
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StorageSecurityException();
    }
}

/// <summary>错误仅暴露分类，日志不包含密钥内容、完整路径或 ACL 主体清单。</summary>
public sealed class StorageSecurityException : IOException
{
    public StorageSecurityException() : base("服务数据权限或路径保护检查失败，需要管理员维护。") { }
}
