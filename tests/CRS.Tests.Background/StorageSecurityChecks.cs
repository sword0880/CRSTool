using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

/// <summary>权限用真实 Windows ACL 验证；只更改测试自己的目录、文件和子进程。</summary>
internal static class StorageSecurityChecks
{
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static int passed;

    /// <summary>拒绝断言只接受保护检查异常，避免把环境或文件操作失败误判为通过。</summary>
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (StorageSecurityException) { Pass(name); return; }
        throw new InvalidOperationException("未拒绝：" + name);
    }

    /// <summary>输出范围明确的验证证据。</summary>
    private static void Pass(string name) { passed++; Console.WriteLine("PASS 权限：" + name); }

    /// <summary>覆盖根权限、受保护子项和文件链接，然后验证真实宿主的定期停止行为。</summary>
    public static async Task RunAsync(string root, string package)
    {
        var directory = Path.Combine(root, "acl-check"); StorageSecurity.Prepare(directory, true);
        var folder = new DirectoryInfo(directory); var original = folder.GetAccessControl();
        StorageSecurity.Validate(directory, true); Pass("封闭目录通过启动校验");
        try
        {
            var widened = folder.GetAccessControl();
            widened.AddAccessRule(new(Users, FileSystemRights.Read, AccessControlType.Allow)); folder.SetAccessControl(widened);
            Reject(() => StorageSecurity.Validate(directory, true), "根目录多出普通用户读取权限时拒绝");
        }
        finally { folder.SetAccessControl(original); }
        try
        {
            var inherited = folder.GetAccessControl(); inherited.SetAccessRuleProtection(false, true); folder.SetAccessControl(inherited);
            Reject(() => StorageSecurity.Validate(directory, true), "根目录重新开放父级继承时拒绝");
        }
        finally { folder.SetAccessControl(original); }
        var file = new FileInfo(Path.Combine(directory, "service-key.dpapi")); File.WriteAllText(file.FullName, "仅测试权限");
        var fileOriginal = file.GetAccessControl();
        try
        {
            var widened = file.GetAccessControl(); widened.SetAccessRuleProtection(true, true);
            widened.AddAccessRule(new(Users, FileSystemRights.Read, AccessControlType.Allow)); file.SetAccessControl(widened);
            Reject(() => StorageSecurity.Validate(directory, true), "受保护密钥文件的额外读取权限也被检测");
        }
        finally { file.SetAccessControl(fileOriginal); }
        var child = Directory.CreateDirectory(Path.Combine(directory, "backup")); var childOriginal = child.GetAccessControl();
        try
        {
            var widened = child.GetAccessControl(); widened.SetAccessRuleProtection(true, true);
            widened.AddAccessRule(new(Users, FileSystemRights.Read, AccessControlType.Allow)); child.SetAccessControl(widened);
            Reject(() => StorageSecurity.Validate(directory, true), "备份子目录独立 ACL 漂移同样拒绝");
        }
        finally { child.SetAccessControl(childOriginal); }
        var link = Path.Combine(directory, "linked-backup"); Directory.CreateSymbolicLink(link, child.FullName);
        try { Reject(() => StorageSecurity.Validate(directory, true), "数据树目录链接不被递归遍历"); }
        finally { Directory.Delete(link); }
        File.Delete(file.FullName); File.CreateSymbolicLink(file.FullName, Path.Combine(root, "scheduler.db"));
        try { Reject(() => ServiceKey.Open(directory), "密钥文件链接在读取或生成前拒绝"); }
        finally { File.Delete(file.FullName); }
        await HostDriftAsync(root, package);
        Console.WriteLine($"Worker 权限专项通过：{passed} 项。");
    }

    /// <summary>真实服务子进程巡检发现漂移后退出；不更改系统已安装的服务。</summary>
    private static async Task HostDriftAsync(string root, string package)
    {
        var data = Path.Combine(root, "acl-process"); var pipe = "CRS.Worker.Acl." + Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(Path.Combine(package, "CRS.Worker.Service.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var value in new[] { "--console", "--data", data, "--plugins", Path.Combine(package, "Plugins"), "--pipe", pipe })
            info.ArgumentList.Add(value);
        using var process = Process.Start(info)!;
        process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        try
        {
            var health = await WorkerProcessReadiness.WaitAsync(new BackgroundPipeClient(pipe),process);
            if (health.Health?.State != "Running") throw new InvalidOperationException("测试宿主未正常启动。");
            var keyPath = Path.Combine(data, "service-key.dpapi"); var keyHash = SHA256.HashData(File.ReadAllBytes(keyPath));
            var file = new FileInfo(keyPath); var original = file.GetAccessControl();
            try
            {
                var widened = file.GetAccessControl(); widened.AddAccessRule(new(Users, FileSystemRights.Read, AccessControlType.Allow));
                file.SetAccessControl(widened);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
                if (process.ExitCode == 0) throw new InvalidOperationException("权限漂移后宿主未以故障退出。");
                Pass("真实宿主在定期巡检发现文件 ACL 漂移后停止并以非零退出");
                if (!keyHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(keyPath))))
                    throw new InvalidOperationException("权限故障不应改变原密钥。");
                Pass("权限异常保留原密钥，不自动修复或重新生成");
            }
            finally { file.SetAccessControl(original); }
        }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
    }
}
