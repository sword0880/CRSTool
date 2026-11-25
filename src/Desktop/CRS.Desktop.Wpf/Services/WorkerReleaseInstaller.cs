using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CRS.Common.Integrity;
using CRS.Worker.Ipc;
using Microsoft.Win32;

namespace CRS.DesktopClient.Services;

internal enum WorkerInstallResult { Completed, Cancelled, PowerShellUnavailable, PackageRejected, MaintenanceFailed }

internal static class WorkerReleaseInstaller
{
    internal static bool HasTrustedPublisher()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var policy = machine.OpenSubKey(@"SOFTWARE\CRS\WorkerUpdates");
            return policy?.GetValue("PublisherThumbprint") is string { Length: 40 } thumbprint &&
                thumbprint.All(char.IsAsciiHexDigit);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool SupportsPackage => RuntimeInformation.OSArchitecture == Architecture.X64;

    internal static string PowerShellPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "PowerShell", "7", "pwsh.exe");

    internal static async Task<WorkerInstallResult> InstallAsync(WorkerGitHubRelease release, string path)
    {
        if (!ContentFingerprint.IsSha256Hex(release.Sha256)) return WorkerInstallResult.PackageRejected;
        if (!File.Exists(PowerShellPath)) return WorkerInstallResult.PowerShellUnavailable;
        using var resource = typeof(WorkerReleaseInstaller).Assembly.GetManifestResourceStream("CRS.WorkerUpdate.Install.ps1")
            ?? throw new InvalidOperationException("无法读取内置后台安装入口。");
        using var script = new StreamReader(resource);
        var pathBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var command = $"$archivePath=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{pathBase64}'))\n" +
            $"$expectedVersion='{release.Version}'\n$expectedHash='{release.Sha256}'\n" +
            $"$expectedProtocol={WorkerProtocol.Version}\n" + await script.ReadToEndAsync().ConfigureAwait(false);
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var argument in new[] { "-NoProfile", "-NoLogo", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) })
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动后台安装。");
            // 管理员进程开始后让它完成维护事务，桌面会话取消不能终止服务升级。
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode switch
            {
                0 => WorkerInstallResult.Completed,
                2 => WorkerInstallResult.PackageRejected,
                _ => WorkerInstallResult.MaintenanceFailed
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            return WorkerInstallResult.Cancelled;
        }
    }
}
