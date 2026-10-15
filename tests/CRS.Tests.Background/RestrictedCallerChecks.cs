using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using CRS.Worker.Ipc;
using Microsoft.Win32.SafeHandles;

/// <summary>以真实受限线程令牌访问跨进程服务，避免创建测试账户或更改桌面 ACL。</summary>
internal static class RestrictedCallerChecks
{
    public static async Task RunAsync()
    {
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var bytes = new byte[adminSid.BinaryLength]; adminSid.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            using var identity = WindowsIdentity.GetCurrent();
            var disable = new SidAndAttributes { Sid = memory };
            if (!CreateRestrictedToken(identity.AccessToken, 1, 1, ref disable, 0, IntPtr.Zero, 0, IntPtr.Zero, out var token)) throw new Win32Exception();
            using (token)
                await WindowsIdentity.RunImpersonatedAsync(token, async () =>
                {
                    if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("测试令牌仍具有管理员角色。");
                    var client = new BackgroundPipeClient();
                    var health = await client.QueryAsync("GetHealth");
                    var plan = (await client.QueryAsync("GetJobs")).Jobs!.Single();
                    var result = await client.SendAsync(new(WorkerProtocol.Version, Guid.NewGuid(), "Pause", plan.PluginId, plan.JobId, ExpectedRevision: plan.Revision));
                    if (health.Code != "Ok" || result.Code != "Forbidden") throw new InvalidOperationException("只读身份管道授权不正确。");
                    Console.WriteLine("PASS 真实移除管理员组的 Windows 令牌：可读健康和计划，管理命令 Forbidden。");
                });
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disableCount, ref SidAndAttributes disable, uint deleteCount, IntPtr deletedPrivileges, uint restrictedCount, IntPtr restrictedSids, out SafeAccessTokenHandle token);
}
