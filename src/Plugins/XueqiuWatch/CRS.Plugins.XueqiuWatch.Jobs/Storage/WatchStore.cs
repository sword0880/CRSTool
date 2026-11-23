using System.Security.Cryptography;
using System.Text;
using CRS.Plugins.XueqiuWatch.Core;
using CRS.Feed;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.XueqiuWatch.Jobs;

internal sealed partial class WatchStore : IDisposable
{
    private const int ApplicationId = 1481723735;
    private readonly SqliteConnection db;

    public WatchStore(string directory, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("插件存储密钥无效。", nameof(key));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "opinions.db");
        var existing = File.Exists(path);
        SQLitePCL.Batteries_V2.Init();
        db = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = Convert.ToHexString(key),
            Mode = existing ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        try
        {
            db.Open();
            if (string.IsNullOrEmpty(Scalar("PRAGMA cipher_version")?.ToString()))
                throw new InvalidDataException("未能启用观点库加密。");
            Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
            var application = Convert.ToInt32(Scalar("PRAGMA application_id"));
            var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
            // SQLite 首次建库事务在提交前中断时，可安全恢复空库。
            if (application == 0 && version == 0 && Convert.ToInt32(Scalar("SELECT count(*) FROM sqlite_schema")) == 0)
            {
                Initialize();
                version = 1;
            }
            else if (application != ApplicationId || version is not (1 or 2 or 3 or 4))
                throw new InvalidDataException("观点库存储格式不兼容。");
            if (version == 1) InitializeLeads();
            if (version < 3) InitializeLeadScope();
            if (version < 4) InitializeAuthorFeeds();
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        using var transaction = db.BeginTransaction();
        Execute($"""
            CREATE TABLE authors(account TEXT PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE securities(market TEXT NOT NULL, code TEXT NOT NULL, body TEXT NOT NULL,
                PRIMARY KEY(market,code));
            CREATE TABLE opinions(id TEXT PRIMARY KEY, account TEXT NOT NULL REFERENCES authors(account),
                market TEXT NOT NULL, code TEXT NOT NULL, posted INTEGER NOT NULL, source TEXT NOT NULL, body TEXT NOT NULL,
                UNIQUE(account,market,code,source));
            CREATE INDEX opinions_by_posted ON opinions(posted);
            CREATE INDEX opinions_by_stock ON opinions(market,code,posted);
            CREATE TABLE changes(id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL);
            PRAGMA application_id={ApplicationId};
            PRAGMA user_version=1;
            """, transaction);
        foreach (var author in WatchProtocol.InitialAuthors)
            Execute("INSERT INTO authors VALUES($account,$body)", transaction,
                ("$account", author.Account), ("$body", WatchProtocol.Encode(author with { Revision = 1 })));
        transaction.Commit();
    }

    public WatchReply Access(WatchRequest request, string payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return request.Operation switch
        {
            "authors" => new(Authors: Authors()),
            "catalog" => new(Securities: Catalog(request.Filter?.Search)),
            "overview" => new(Overview: Overview(ValidateFilter(request.Filter), token)),
            "detail" => new(Detail: Details(ValidateFilter(request.Filter), token)),
            "leads" => new() { Leads = ReadLeads(ValidateFilter(request.Filter)) },
            "author-save" or "opinion-save" => Save(request, payload, token),
            _ => throw new ArgumentException("未知的观点追踪操作。")
        };
    }

    private WatchReply Save(WatchRequest request, string payload, CancellationToken token)
    {
        if (request.ChangeId == Guid.Empty) throw new ArgumentException("保存请求缺少标识。");
        using var transaction = db.BeginTransaction();
        var id = request.ChangeId.ToString("N");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        if (Scalar("SELECT fingerprint FROM changes WHERE id=$id", transaction, ("$id", id)) is string prior)
        {
            if (prior != hash) throw new ArgumentException("保存参数已改变，请重新提交。");
            return new();
        }
        switch (request.Operation)
        {
            case "author-save": SaveAuthor(request.Author, transaction); break;
            case "opinion-save": SaveOpinion(request.Opinion, transaction); break;
        }
        Execute("INSERT INTO changes VALUES($id,$hash)", transaction, ("$id", id), ("$hash", hash));
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return new();
    }

    private void SaveAuthor(Author? input, SqliteTransaction transaction)
    {
        if (input is null) throw new ArgumentException("请填写作者资料。");
        var account = WatchInput.AccountId(input.Account);
        var existing = Scalar("SELECT body FROM authors WHERE account=$account", transaction, ("$account", account)) as string;
        var old = existing is null ? null : WatchProtocol.Decode<Author>(existing);
        if (input.Revision != (old?.Revision ?? 0))
            throw new ArgumentException("此账号已存在或刚刚被修改，请刷新观察名单。");
        if (old is null && Convert.ToInt32(Scalar("SELECT count(*) FROM authors", transaction)) >= WatchProtocol.MaximumAuthors)
            throw new ArgumentException("观察名单已达到支持的账号数量。");

        var name = WatchInput.Text(input.Name, 40, true);
        var note = WatchInput.Text(input.Note, 80);
        var feedUrl = string.IsNullOrWhiteSpace(input.FeedUrl) ? string.Empty : FeedAddress.Normalize(input.FeedUrl);
        var previousFeedUrl = old?.FeedUrl ?? string.Empty;
        var saved = new Author(account, name, note, input.Active && !input.Archived,
            input.Archived, input.Revision + 1, feedUrl);
        Execute("""
            INSERT INTO authors VALUES($account,$body)
            ON CONFLICT(account) DO UPDATE SET body=excluded.body
            """, transaction, ("$account", account), ("$body", WatchProtocol.Encode(saved)));
        if (!string.Equals(previousFeedUrl, feedUrl, StringComparison.Ordinal))
        {
            // 修改来源后清除旧验证器和覆盖状态，但保留已发现的历史线索。
            var resetState = string.IsNullOrWhiteSpace(feedUrl) ? "NotConfigured" : "NeverChecked";
            Execute("""
                UPDATE lead_coverage
                SET state=$state, observed=0, matches=-1, etag=NULL, last_modified=NULL
                WHERE account=$account
                """, transaction, ("$state", resetState), ("$account", account));
        }
    }

    private void SaveOpinion(Opinion? input, SqliteTransaction transaction)
    {
        if (input is null) throw new ArgumentException("请填写观点资料。");
        WatchInput.CheckOpinion(input);
        var account = WatchInput.AccountId(input.Account);
        if (Scalar("SELECT 1 FROM authors WHERE account=$a", transaction, ("$a", account)) is null)
            throw new ArgumentException("请先在观察名单登记作者。");
        var id = input.Id.ToString("N");
        var previous = Scalar("SELECT body FROM opinions WHERE id=$id", transaction, ("$id", id)) as string;
        var old = previous is null ? null : WatchProtocol.Decode<Opinion>(previous);
        if (input.Revision != (old?.Revision ?? 0))
            throw new ArgumentException("此记录刚刚被修改，请刷新后再编辑。");

        var security = input.Security is null ? null : WatchInput.CheckSecurity(input.Security);
        var published = input.PublishedAt.ToOffset(TimeSpan.FromHours(8));
        var url = WatchInput.SourceLink(input.Url);
        if (input.Attribution == Attribution.Author && url.Length > 0 &&
            !url.StartsWith($"https://xueqiu.com/{account}/", StringComparison.Ordinal))
            throw new ArgumentException("原帖链接的作者 ID 与选定作者不同，请核对归属。");
        var content = WatchInput.Text(input.Content, 700, true);
        var evidence = WatchInput.Text(input.Evidence, 700);
        var saved = new Opinion(input.Id, account, security, published, input.Stance,
            input.Attribution, content, evidence, url, WatchInput.Text(input.Question, 180),
            WatchInput.Text(input.Finding, 180), input.Revision + 1, old?.RecordedAt ?? WatchProtocol.BeijingNow);
        // 对未附原帖的笔记，后续编辑正文或摘录时继续使用首次录入的指纹。
        var source = old is { Url.Length: 0 } && url.Length == 0
            ? (string)Scalar("SELECT source FROM opinions WHERE id=$id", transaction, ("$id", id))!
            : SourceKey(saved);
        var market = security?.Market ?? "";
        var code = security?.Code ?? "";

        if (Scalar("""
            SELECT 1 FROM opinions WHERE account=$a AND market=$m AND code=$c AND source=$s AND id<>$id
            """, transaction, ("$a", account), ("$m", market), ("$c", code), ("$s", source), ("$id", id)) is not null)
            throw new ArgumentException("此发言已记录在该股票下，请打开现有记录编辑。");

        if (security is not null)
            Execute("""
                INSERT INTO securities VALUES($m,$c,$body)
                ON CONFLICT(market,code) DO UPDATE SET body=excluded.body
                """, transaction, ("$m", market), ("$c", code), ("$body", WatchProtocol.Encode(security)));
        Execute("""
            INSERT INTO opinions VALUES($id,$a,$m,$c,$published,$s,$body)
            ON CONFLICT(id) DO UPDATE SET account=excluded.account,market=excluded.market,code=excluded.code,
                posted=excluded.posted,source=excluded.source,body=excluded.body
            """, transaction,
            ("$id", id), ("$a", account), ("$m", market), ("$c", code), ("$published", published.ToUnixTimeSeconds()),
            ("$s", source), ("$body", WatchProtocol.Encode(saved)));
    }

    private static string SourceKey(Opinion record)
    {
        if (record.Url.Length != 0) return record.Url;
        var text = record.Evidence.Length == 0 ? record.Content : record.Evidence;
        var value = $"{record.PublishedAt.ToUnixTimeSeconds()}\n{text}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private object? Scalar(string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteScalar();
    }

    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    public void Dispose() => db.Dispose();
}
