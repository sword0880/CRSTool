using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal sealed class GoogleAlertsSync(WorkerWatchClient client)
{
    private const string Alerts = "https://www.google.com/alerts?hl=en";
    private static readonly string Profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CRS", "XueqiuWatch", "GoogleAlertsBrowser");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static void OpenSignIn()
    {
        var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "Edge", "Application", "msedge.exe"))
            .FirstOrDefault(File.Exists) ?? throw new ArgumentException("请先安装 Microsoft Edge，再连接 Google Alerts。");
        Directory.CreateDirectory(Profile);
        var start = new ProcessStartInfo(edge) { UseShellExecute = false };
        start.ArgumentList.Add($"--user-data-dir={Profile}");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--disable-background-mode");
        start.ArgumentList.Add(Alerts);
        try { using var process = Process.Start(start); }
        catch (Win32Exception) { throw new ArgumentException("无法打开用于连接 Google Alerts 的 Edge 窗口。"); }
    }

    internal async Task<string> SyncAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var state = (await client.AskAsync(new("lead-subscription"), token)).Subscription
                ?? throw new InvalidDataException("配套后台尚不支持自动同步订阅。");
            if (state.Scope.Length == 0) return "当前没有启用的作者。";
            if (!state.Pending) return "Google RSS 订阅已与观察名单同步。";
            if (!Directory.Exists(Profile)) return "订阅待同步。请先连接 Google，已有线索可继续查看。";

            var url = await UpdateAlertAsync(state, token);
            await client.SaveAsync(new("lead-feed-sync")
            {
                FeedUrl = url, FeedScope = state.Scope, PreviousFeedFingerprint = state.FeedFingerprint
            }, token);
            return "Google RSS 订阅已更新。后台将按原计划查找发言线索。";
        }
        catch (GoogleAlertsException error) { return $"订阅待同步。{error.Message}"; }
        catch (ArgumentException error) { return $"订阅待同步。{error.Message}"; }
        catch (IOException) { return "订阅待同步。请确认正在运行支持自动订阅的 CRS 后台服务。"; }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            // 浏览器异常可能含当前网址和页面数据，界面只显示可操作的提示。
            return "订阅待同步。请关闭用于连接 Google 的 Edge 窗口后重试，并检查 Google Alerts 是否可访问。";
        }
        finally { Gate.Release(); }
    }

    private static async Task<string> UpdateAlertAsync(LeadSubscription state, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchPersistentContextAsync(Profile, new()
        {
            Channel = "msedge", Headless = true, Timeout = 12_000, AcceptDownloads = false
        });
        var page = browser.Pages.FirstOrDefault() ?? await browser.NewPageAsync();
        page.SetDefaultTimeout(8_000);
        await page.GotoAsync(Alerts, new() { WaitUntil = WaitUntilState.Load, Timeout = 15_000 });
        return await new GoogleAlertsPage(page).ApplyAsync(state, token);
    }
}

internal sealed class GoogleAlertsException(string message) : Exception(message);
