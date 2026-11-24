using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal static class GoogleAlertsDesktopSync
{
    private static readonly SemaphoreSlim BrowserProfile = new(1, 1);

    internal static async Task<string?> RunAsync(WorkerWatchClient client, CancellationToken token,
        string? authorAccount = null, bool connect = false)
    {
        await BrowserProfile.WaitAsync(token);
        try
        {
            var connected = (await client.AskAsync(new("alert-account"), token)).ConnectedGoogleAccount;
            if (!connect && string.IsNullOrEmpty(connected)) return null;
            var authors = (await client.AskAsync(new("authors"), token)).Authors ?? [];
            var pending = authors.Where(author => (authorAccount is null || author.Account == authorAccount) &&
                author.AlertState is GoogleAlertState.PendingCreate or GoogleAlertState.Creating or
                    GoogleAlertState.PendingDelete or GoogleAlertState.Deleting).ToArray();
            if (!connect && pending.Length == 0) return null;

            GoogleAlertsPageSession session;
            try { session = await GoogleAlertsPageSession.OpenAsync(connect, token); }
            catch (TimeoutException)
            { throw new ArgumentException("Google 快讯浏览器启动或页面导航超时（20 秒），请核对 Chrome、网络和专用浏览器配置。"); }
            catch (PlaywrightException)
            { throw new ArgumentException("Google 快讯无法启动专用 Chrome 或打开页面，请核对 Chrome 安装及网络。"); }
            await using var page = session;
            var account = await page.ReadAccountAsync(connect, token);
            if (!string.IsNullOrEmpty(connected) && account != connected)
                throw new ArgumentException("当前 Google 账号与 CRS 连接不一致。请在专用 Chrome 中切回原账号。");
            if (connect && string.IsNullOrEmpty(connected))
                await client.SaveAsync(new("alert-account-save", ConnectedGoogleAccount: account), token);

            var synced = 0;
            var pendingResult = 0;
            string? failedStage = null;
            foreach (var author in pending)
            {
                token.ThrowIfCancellationRequested();
                var subscription = (await client.AskAsync(new("alert-subscription", new(Account: author.Account)), token)).Alert;
                if (subscription is null) continue;
                try
                {
                    if (await SyncAuthorAsync(client, page, author, subscription, account, token)) synced++;
                    else pendingResult++;
                }
                catch (ArgumentException)
                {
                    // 单个作者的重复提醒、账号不匹配或页面状态未确认不阻断其他作者。
                    pendingResult++;
                    failedStage ??= page.DiagnosticStage;
                }
                catch (TimeoutException)
                {
                    // 远端操作可能已提交，保留原同步状态并停止本轮，禁止盲目重试。
                    throw new ArgumentException($"Google 快讯操作超时：{page.DiagnosticStage}。请重新同步核对远端状态。");
                }
                catch (PlaywrightException)
                {
                    throw new ArgumentException($"Google 快讯页面操作失败：{page.DiagnosticStage}。请核对页面后重新同步。");
                }
            }
            return pendingResult == 0 ? $"Google 快讯已连接，本次同步 {synced} 个作者。" :
                $"Google 快讯已处理 {synced} 个作者，{pendingResult} 个待网页核对。{(failedStage is null ? "" : $"首个异常阶段：{failedStage}。")}";
        }
        catch (TimeoutException)
        {
            throw new ArgumentException("Google 快讯登录状态检查超时，请完成登录后返回快讯管理页面。");
        }
        catch (PlaywrightException)
        {
            throw new ArgumentException("无法同步 Google 快讯。请确认已安装 Chrome，网页可访问且专用浏览器已登录。");
        }
        finally { BrowserProfile.Release(); }
    }

    private static async Task<bool> SyncAuthorAsync(WorkerWatchClient client, GoogleAlertsPageSession page,
        Author author, GoogleAlertSubscription subscription, string account, CancellationToken token)
    {
        var update = new GoogleAlertUpdate(author.Account, subscription.OperationId, account,
            RemoteId: subscription.RemoteId);
        if (subscription.State is GoogleAlertState.PendingDelete or GoogleAlertState.Deleting)
        {
            await client.SaveAsync(new("alert-sync-begin", Alert: update), token);
            await page.DeleteAsync(subscription.RemoteId, account, token);
            await client.SaveAsync(new("alert-sync-complete", Alert: update with { Outcome = GoogleAlertOutcome.Missing }), token);
            return true;
        }
        if (subscription.State is not (GoogleAlertState.PendingCreate or GoogleAlertState.Creating)) return false;

        var query = WatchInput.GoogleAlertQuery(author.Account);
        // 已存在且使用 RSS 投递的同查询提醒可以复用，登记为外部提醒，保留用户的远端管理权。
        var existing = await page.FindByQueryAsync(query, token);
        if (await page.ReadAccountAsync(false, token) != account)
            throw new ArgumentException("Google 账号发生变化，已停止同步。");
        if (existing is { FeedUrl.Length: 0 })
            throw new ArgumentException("该查询已有提醒，请核对网页投递方式。");
        await client.SaveAsync(new("alert-sync-begin", Alert: update), token);
        if (existing is not null)
        {
            await client.SaveAsync(new("alert-sync-complete", Alert: update with
            {
                Outcome = GoogleAlertOutcome.Existing, FeedUrl = existing.FeedUrl, RemoteId = existing.Id
            }), token);
            return true;
        }
        if (subscription.State == GoogleAlertState.Creating || !author.Active || author.Archived)
        {
            // 上次创建结果未知。当前列表未见该提醒，先归还为待创建，下一次同步再执行。
            await client.SaveAsync(new("alert-sync-complete", Alert: update with { Outcome = GoogleAlertOutcome.Missing }), token);
            return false;
        }
        var created = await page.CreateRssAsync(query, account, token);
        await client.SaveAsync(new("alert-sync-complete", Alert: update with
        {
            Outcome = GoogleAlertOutcome.Created, FeedUrl = created.FeedUrl, RemoteId = created.Id
        }), token);
        return true;
    }
}
