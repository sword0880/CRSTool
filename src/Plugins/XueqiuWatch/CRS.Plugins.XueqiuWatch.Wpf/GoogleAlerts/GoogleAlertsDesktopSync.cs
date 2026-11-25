using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Playwright;

namespace CRS.Plugins.XueqiuWatch.Wpf;

internal static class GoogleAlertsDesktopSync
{
    private static readonly SemaphoreSlim BrowserProfile = new(1, 1);

    internal static async Task<string?> RunAsync(WorkerWatchClient client, CancellationToken token,
        string? authorAccount = null, bool connect = false, bool connectIfNeeded = false,
        Action<int, int>? reportProgress = null, bool onlyIfAccountChanged = false)
    {
        await BrowserProfile.WaitAsync(token);
        try
        {
            var connected = (await client.AskAsync(new("alert-account"), token)).ConnectedGoogleAccount ?? "";
            if (onlyIfAccountChanged && connected.Length == 0) return null;
            if (!connect && !connectIfNeeded && connected.Length == 0) return null;
            Author[] pending = connect ? [] : await GetCandidatesAsync(client, authorAccount, verifyBound: false, token);
            if (!connect && pending.Length == 0) return null;
            var canBindAccount = connectIfNeeded &&
                pending.Any(author => author.Active && !author.Archived &&
                    author.AlertState is GoogleAlertState.PendingCreate or GoogleAlertState.Creating);
            if (!connect && !canBindAccount && connected.Length == 0) return null;

            GoogleAlertsPageSession session;
            try { session = await GoogleAlertsPageSession.OpenAsync(token); }
            catch (TimeoutException)
            { throw new ArgumentException("Google 快讯浏览器启动或页面导航超时（20 秒），请核对 Chrome、网络和专用浏览器配置。"); }
            catch (PlaywrightException)
            { throw new ArgumentException("Google 快讯无法打开。请先关闭用于登录的专用 Chrome，再核对浏览器和网络后重试。"); }
            await using var page = session;
            var account = await page.ReadAccountAsync(token);
            if (onlyIfAccountChanged && account == connected) return null;
            var switched = connect && connected.Length > 0 && account != connected;
            if (connect)
            {
                await client.SaveAsync(new("alert-account-switch", ConnectedGoogleAccount: account,
                    ExpectedGoogleAccount: connected), token);
                pending = await GetCandidatesAsync(client, authorAccount, verifyBound: true, token);
            }
            else if (connected.Length > 0 && account != connected)
                throw new ArgumentException("检测到不同的 Google 账号。请点击「同步全部作者」，在当前账号核实订阅。");
            else if (canBindAccount && connected.Length == 0)
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
            var prefix = switched ? "已切换 Google 快讯账号。" : "";
            if (pending.Length == 0) return $"{prefix}观察名单没有需要核实或同步的 Google 快讯。";
            return pendingResult == 0 ? $"{prefix}Google 快讯已核实或同步 {synced} 位作者。" :
                $"{prefix}Google 快讯已处理 {synced} 位作者，{pendingResult} 位待核实。{(failedStage is null ? "" : $"首个异常阶段：{failedStage}。")}";
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

    private static async Task<Author[]> GetCandidatesAsync(WorkerWatchClient client, string? account, bool verifyBound,
        CancellationToken token)
    {
        var authors = (await client.AskAsync(new("authors"), token)).Authors ?? [];
        return authors.Where(author => (account is null || author.Account == account) &&
            (author.AlertState is GoogleAlertState.PendingCreate or GoogleAlertState.Creating or
                GoogleAlertState.PendingDelete or GoogleAlertState.Deleting ||
             verifyBound && !author.Archived && author.UsesGoogleAlertFeed &&
                author.AlertState is GoogleAlertState.Managed or GoogleAlertState.External)).ToArray();
    }

    private static async Task<bool> SyncAuthorAsync(WorkerWatchClient client, GoogleAlertsPageSession page,
        Author author, GoogleAlertSubscription subscription, string account, CancellationToken token)
    {
        var query = WatchInput.GoogleAlertQuery(author.Account);
        if (subscription.State is GoogleAlertState.Managed or GoogleAlertState.External)
        {
            var alert = await page.FindByQueryAsync(query, token);
            await page.VerifyAccountAsync(account, token);
            var outcome = alert is null ? GoogleAlertOutcome.Missing :
                alert.FeedUrl.Length == 0 ? GoogleAlertOutcome.NonRss : GoogleAlertOutcome.Existing;
            await client.SaveAsync(new("alert-sync-verify", Alert: new(author.Account, Guid.Empty, account,
                outcome, alert?.Id ?? "", alert?.FeedUrl ?? "", author.Revision)), token);
            if (outcome == GoogleAlertOutcome.NonRss)
                throw new ArgumentException("该查询已存在，但 Google 快讯没有提供 RSS 地址。请在网页核对投递方式。");
            if (outcome == GoogleAlertOutcome.Existing || !author.Active)
                return outcome == GoogleAlertOutcome.Existing;
            var renewed = (await client.AskAsync(new("alert-subscription", new(Account: author.Account)), token)).Alert;
            if (renewed is not { State: GoogleAlertState.PendingCreate }) return false;
            subscription = renewed;
        }

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

        // 已存在且使用 RSS 投递的同查询提醒可以复用，登记为外部提醒，保留用户的远端管理权。
        var existing = subscription.State == GoogleAlertState.Creating
            ? await page.RecheckByQueryAsync(query, account, token)
            : await page.FindByQueryAsync(query, token);
        await page.VerifyAccountAsync(account, token);
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
