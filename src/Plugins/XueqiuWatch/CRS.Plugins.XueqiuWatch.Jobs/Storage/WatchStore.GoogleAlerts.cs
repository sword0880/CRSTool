using CRS.Feed;
using CRS.Plugins.XueqiuWatch.Core;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore
{
    private void InitializeGoogleAlerts()
    {
        using var transaction = db.BeginTransaction();
        Execute("""
            CREATE TABLE google_alerts(account TEXT PRIMARY KEY REFERENCES authors(account), body TEXT NOT NULL,
                google_account TEXT NOT NULL, remote_id TEXT NOT NULL);
            CREATE UNIQUE INDEX google_alert_owner ON google_alerts(google_account, remote_id) WHERE remote_id <> '';
            PRAGMA user_version=7;
            """, transaction);
        foreach (var author in Authors(transaction)) ReconcileGoogleAlert(author, null, transaction);
        transaction.Commit();
    }

    private Author RequireAuthor(string? input, SqliteTransaction? transaction = null)
    {
        var account = WatchInput.AccountId(input);
        if (Scalar("SELECT body FROM authors WHERE account=$account", transaction, ("$account", account)) is not string body)
            throw new ArgumentException("请先在观察名单登记作者。");
        return WatchProtocol.Decode<Author>(body);
    }

    private GoogleAlertSubscription ReadGoogleAlert(Author author) => ReadStoredGoogleAlert(author.Account)
        ?? new(author.Account, author.Archived || author.FeedUrl.Length == 0
            ? GoogleAlertState.None : GoogleAlertState.External);

    private GoogleAlertSubscription? ReadStoredGoogleAlert(string account, SqliteTransaction? transaction = null) =>
        Scalar("SELECT body FROM google_alerts WHERE account=$account", transaction, ("$account", account)) is string body
            ? WatchProtocol.Decode<GoogleAlertSubscription>(body) : null;

    private void ReconcileGoogleAlert(Author author, GoogleAlertSubscription? alert, SqliteTransaction transaction)
    {
        switch (alert?.State)
        {
            case GoogleAlertState.Creating or GoogleAlertState.Deleting:
                // 远端可能已变更，保留操作标识，等待同一账号核对结果。
                return;
            case GoogleAlertState.Managed when author.Archived:
                WriteGoogleAlert(alert with { State = GoogleAlertState.PendingDelete, OperationId = Guid.NewGuid() }, transaction);
                return;
            case GoogleAlertState.PendingDelete when !author.Archived:
                WriteGoogleAlert(alert with { State = GoogleAlertState.Managed, OperationId = Guid.Empty }, transaction);
                return;
            case GoogleAlertState.Managed or GoogleAlertState.PendingDelete:
                return;
        }

        if (author.Active && author.FeedUrl.Length == 0)
        {
            if (alert is null)
                WriteGoogleAlert(new(author.Account, GoogleAlertState.PendingCreate, Guid.NewGuid()), transaction);
        }
        else if (alert is not null)
            DeleteGoogleAlert(author.Account, transaction);
    }

    private void BeginGoogleAlert(GoogleAlertUpdate? input, SqliteTransaction transaction)
    {
        var (author, alert, googleAccount) = ValidateGoogleAlertOperation(input, transaction);
        var nextState = alert.State switch
        {
            GoogleAlertState.PendingCreate => GoogleAlertState.Creating,
            GoogleAlertState.PendingDelete => GoogleAlertState.Deleting,
            GoogleAlertState.Creating or GoogleAlertState.Deleting => alert.State,
            _ => throw new ArgumentException("订阅同步状态已改变，请刷新后重试。")
        };
        if (nextState == GoogleAlertState.Deleting && input!.RemoteId != alert.RemoteId)
            throw new ArgumentException("只能删除本次操作绑定的 Google Alerts 提醒。");
        if (nextState == alert.State) return;
        WriteGoogleAlert(alert with { State = nextState, GoogleAccount = googleAccount }, transaction);
        if (nextState == GoogleAlertState.Deleting)
            ChangeGoogleAlertFeed(author, "", transaction);
    }

    private void CompleteGoogleAlert(GoogleAlertUpdate? input, SqliteTransaction transaction)
    {
        var (author, alert, _) = ValidateGoogleAlertOperation(input, transaction);
        if (alert.State == GoogleAlertState.Deleting)
        {
            if (input!.Outcome != GoogleAlertOutcome.Missing || input.RemoteId != alert.RemoteId)
                throw new ArgumentException("请先核实当前提醒已从关联的 Google 账号移除。");
        }
        else if (alert.State == GoogleAlertState.Creating)
        {
            if (input!.Outcome is GoogleAlertOutcome.Created or GoogleAlertOutcome.Existing)
            {
                var remoteId = WatchInput.Text(input.RemoteId, 256, true);
                var feedUrl = GoogleAlertFeedUrl(input.FeedUrl);
                if (input.Outcome == GoogleAlertOutcome.Created)
                {
                    if (Scalar("SELECT 1 FROM google_alerts WHERE google_account=$google AND remote_id=$remote AND account<>$author",
                        transaction, ("$google", alert.GoogleAccount), ("$remote", remoteId), ("$author", author.Account)) is not null)
                        throw new ArgumentException("此 Google Alerts 提醒已绑定到其他作者。");
                    ChangeGoogleAlertFeed(author, feedUrl, transaction);
                    var state = author.Archived ? GoogleAlertState.PendingDelete : GoogleAlertState.Managed;
                    WriteGoogleAlert(alert with
                    {
                        State = state, RemoteId = remoteId,
                        OperationId = author.Archived ? Guid.NewGuid() : Guid.Empty
                    }, transaction);
                    return;
                }
                if (!author.Archived) ChangeGoogleAlertFeed(author, feedUrl, transaction);
                DeleteGoogleAlert(author.Account, transaction);
                return;
            }
            if (input.Outcome != GoogleAlertOutcome.Missing)
                throw new ArgumentException("请先核实 Google Alerts 同步结果。");
        }
        else
            throw new ArgumentException("订阅同步状态已改变，请刷新后重试。");

        DeleteGoogleAlert(author.Account, transaction);
        ReconcileGoogleAlert(author, null, transaction);
    }

    private (Author Author, GoogleAlertSubscription Alert, string GoogleAccount) ValidateGoogleAlertOperation(
        GoogleAlertUpdate? input, SqliteTransaction transaction)
    {
        if (input is null || input.OperationId == Guid.Empty)
            throw new ArgumentException("缺少 Google Alerts 同步操作标识。");
        var author = RequireAuthor(input.AuthorAccount, transaction);
        var alert = ReadStoredGoogleAlert(author.Account, transaction);
        if (alert is null || alert.OperationId != input.OperationId)
            throw new ArgumentException("订阅同步状态已改变，请刷新后重试。");
        var googleAccount = WatchInput.Text(input.GoogleAccount, 256, true);
        if (alert.GoogleAccount.Length != 0 && !string.Equals(alert.GoogleAccount, googleAccount, StringComparison.Ordinal))
            throw new ArgumentException("当前 Google 账号与订阅记录不一致。");
        return (author, alert, googleAccount);
    }

    private static string GoogleAlertFeedUrl(string? value)
    {
        var url = FeedAddress.Normalize(value ?? "");
        var uri = new Uri(url);
        var parts = uri.AbsolutePath.Split('/');
        if (uri.Host is not ("google.com" or "www.google.com") || !uri.IsDefaultPort || uri.Query.Length != 0 ||
            parts is not ["", "alerts", "feeds", var account, var feed] || account.Length == 0 || feed.Length == 0 ||
            account.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            feed.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("同步结果没有有效的 Google Alerts RSS 地址。");
        return url;
    }

    private void ChangeGoogleAlertFeed(Author author, string feedUrl, SqliteTransaction transaction)
    {
        if (author.FeedUrl != feedUrl)
            WriteAuthor(author with { FeedUrl = feedUrl, Revision = author.Revision + 1 }, author.FeedUrl, transaction);
    }

    private void WriteGoogleAlert(GoogleAlertSubscription alert, SqliteTransaction transaction) => Execute("""
        INSERT INTO google_alerts(account,body,google_account,remote_id) VALUES($account,$body,$google,$remote)
        ON CONFLICT(account) DO UPDATE SET body=excluded.body, google_account=excluded.google_account, remote_id=excluded.remote_id
        """, transaction, ("$account", alert.AuthorAccount), ("$body", WatchProtocol.Encode(alert)),
        ("$google", alert.GoogleAccount), ("$remote", alert.RemoteId));

    private void DeleteGoogleAlert(string account, SqliteTransaction transaction) =>
        Execute("DELETE FROM google_alerts WHERE account=$account", transaction, ("$account", account));
}
