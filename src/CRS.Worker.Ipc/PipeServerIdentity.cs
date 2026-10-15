using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace CRS.Worker.Ipc;

/// <summary>发送管理请求前核对 SCM 实际 PID，避免服务停止时连接同名伪造管道。</summary>
internal static class PipeServerIdentity
{
    public static void Verify(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pipeProcess)) throw new Win32Exception();
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var service = OpenService(manager, "CRSBackgroundService", 4); // 仅查询状态，不申请服务控制权限。
            if (service == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception();
                if (status.ProcessId == 0 || status.ProcessId != pipeProcess || status.CurrentState != 4)
                    throw new UnauthorizedAccessException("管道服务端不属于 SCM 当前运行的 CRS 后台服务。");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
