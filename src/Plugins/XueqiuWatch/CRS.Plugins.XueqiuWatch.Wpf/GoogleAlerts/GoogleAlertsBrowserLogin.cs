using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal static class GoogleAlertsBrowserLogin
{
    internal const string AlertsAddress = "https://www.google.com.hk/alerts?hl=zh-CN";

    internal static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CRS", "GoogleAlertsBrowser");

    internal static Process Open()
    {
        var chrome = FindChrome() ?? throw new ArgumentException("未找到 Google Chrome。请安装 Chrome 后再打开 Google 登录。");
        try
        {
            return Process.Start(CreateStartInfo(chrome))
                ?? throw new ArgumentException("无法打开 Chrome。请检查浏览器安装后重试。");
        }
        catch (Win32Exception)
        {
            throw new ArgumentException("无法打开 Chrome。请检查浏览器安装后重试。");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string chrome)
    {
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        start.ArgumentList.Add("--user-data-dir=" + ProfileDirectory);
        start.ArgumentList.Add("--disable-background-mode");
        start.ArgumentList.Add("--new-window");
        start.ArgumentList.Add(AlertsAddress);
        return start;
    }

    private static string? FindChrome()
    {
        return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(folder => Environment.GetFolderPath(folder))
            .Where(path => path.Length > 0)
            .Select(path => Path.Combine(path, "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
    }
}

internal sealed class GoogleAlertsSignInRequiredException(string message) : ArgumentException(message);
