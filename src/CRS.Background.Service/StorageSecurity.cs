using System.Security.AccessControl;
using System.Security.Principal;
namespace CRS.Background.Service;

/// <summary>机器 DPAPI 依赖文件 ACL；开发控制台也必须封闭密钥目录。</summary>
public static class StorageSecurity
{
    public static void Prepare(string directory, bool console)
    {
        for (var current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("服务数据路径不可包含重解析点。");
        Directory.CreateDirectory(directory);
        if (!console) return; // 正式目录由管理员安装器设置服务 SID 的继承权限。
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var owner = WindowsIdentity.GetCurrent().User!; security.SetOwner(owner);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
