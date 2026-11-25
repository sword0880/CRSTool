using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal static class GoogleAlertsDesktopSync
{
    private static readonly SemaphoreSlim BrowserProfile = new(1, 1);

    internal static async Task<string?> RunAsync(WorkerWatchClient client, CancellationToken token,
        string? authorAccount = null, bool connect = false, bool connectIfNeeded = false,
        Action<int, int>? reportProgress = null)
    {
        await BrowserProfile.WaitAsync(token);
        try
        {
            var connected = (await client.AskAsync(new("alert-account"), token)).ConnectedGoogleAccount;
            if (!connect && !connectIfNeeded && string.IsNullOrEmpty(connected)) return null;
            var authors = (await client.AskAsync(new("authors"), token)).Authors ?? [];
            var pending = authors.Where(author => (authorAccount is null || author.Account == authorAccount) &&
                author.AlertState is GoogleAlertState.PendingCreate or GoogleAlertState.Creating or
                    GoogleAlertState.PendingDelete or GoogleAlertState.Deleting).ToArray();
            if (pending.Length == 0)
            {
                if (!connect) return null;
                if (!string.IsNullOrEmpty(connected)) return "观察名单没有待同步的 Google 快讯。";
            }
            var canBindAccount = connect || (connectIfNeeded &&
                pending.Any(author => author.Active && !author.Archived &&
                    author.AlertState is GoogleAlertState.PendingCreate or GoogleAlertState.Creating));
            if (!canBindAccount && string.IsNullOrEmpty(connected)) return null;

            GoogleAlertsPageSession session;
            try { session = await GoogleAlertsPageSession.OpenAsync(token); }
            catch (TimeoutException)
            { throw new ArgumentException("Google 快讯浏览器启动或页面导航超时（20 秒），请核对 Chrome、网络和专用浏览器配置。"); }
            catch (PlaywrightException)
            { throw new ArgumentException("Google 快讯无法打开。请先关闭用于登录的专用 Chrome，再核对浏览器和网络后重试。"); }
            await using var page = session;
            var account = await page.ReadAccountAsync(token);
            if (!string.IsNullOrEmpty(connected) && account != connected)
                throw new ArgumentException("当前 Google 账号与 CRS 连接不一致。请在专用 Chrome 中切回原账号。");
            if (canBindAccount && string.IsNullOrEmpty(connected))
                await client.SaveAsync(new("alert-account-save", ConnectedGoogleAccount: account), token);

            var synced = 0;
            var pendingResult = 0;
            var current = 0;
            var reload = false;
            string? failedStage = null;
            foreach (var author in pending)
            {
                token.ThrowIfCancellationRequested();
                reportProgress?.Invoke(++current, pending.Length);
                var subscription = (await client.AskAsync(new("alert-subscription", new(Account: author.Account)), token)).Alert;
                try
                {
                    if (reload)
                    {
                        await page.ReloadAsync(account, token);
                        reload = false;
                    }
                    if (subscription is not null && await SyncAuthorAsync(client, page, author, subscription, account, token))
                        synced++;
                    else pendingResult++;
                }
                catch (GoogleAlertsSignInRequiredException) { throw; }
                catch (ArgumentException error)
                {
                    // 单个作者的重复提醒、账号不匹配或页面状态未确认不阻断其他作者。
                    pendingResult++;
                    reload = true;
                    failedStage ??= $"{page.DiagnosticStage}（{error.Message}）";
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
            if (pending.Length == 0) return "Google 快讯已连接，观察名单没有待同步项。";
            return pendingResult == 0 ? $"Google 快讯同步完成，本次处理 {synced} 位作者。" :
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
        var existing = subscription.State == GoogleAlertState.Creating
            ? await page.RecheckByQueryAsync(query, account, token)
            : await page.FindByQueryAsync(query, token);
        if (await page.ReadAccountAsync(token) != account)
            throw new ArgumentException("Google 账号发生变化，已停止同步。");
        if (existing is { FeedUrl.Length: 0 })
            throw new ArgumentException("该查询已有提醒，请核对网页投递方式。");
        if (existing is not null)
        {
            await client.SaveAsync(new("alert-sync-begin", Alert: update), token);
            await client.SaveAsync(new("alert-sync-complete", Alert: update with
            {
                Outcome = GoogleAlertOutcome.Existing, FeedUrl = existing.FeedUrl, RemoteId = existing.Id
            }), token);
            return true;
        }
        if (subscription.State == GoogleAlertState.Creating)
        {
            // 重新加载后的快讯列表已确认没有上次的提醒，取得新的操作标识再继续。
            await client.SaveAsync(new("alert-sync-complete", Alert: update with { Outcome = GoogleAlertOutcome.Missing }), token);
            if (!author.Active || author.Archived) return true;
            var renewed = (await client.AskAsync(new("alert-subscription", new(Account: author.Account)), token)).Alert;
            if (renewed is not { State: GoogleAlertState.PendingCreate }) return false;
            update = update with { OperationId = renewed.OperationId };
        }
        if (!author.Active || author.Archived) return false;
        await page.PrepareRssAsync(query, account, token);
        // 草稿阶段失败仍可直接重试。只有按钮可点击、RSS 投递已确认后才写入待核对状态。
        await client.SaveAsync(new("alert-sync-begin", Alert: update), token);
        var created = await page.SubmitRssAsync(query, account, token);
        await client.SaveAsync(new("alert-sync-complete", Alert: update with
        {
            Outcome = GoogleAlertOutcome.Created, FeedUrl = created.FeedUrl, RemoteId = created.Id
        }), token);
        return true;
    }
}
