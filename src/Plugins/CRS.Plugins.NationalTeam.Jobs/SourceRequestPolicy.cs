using System.Globalization;
using System.IO;
using CRS.Plugins.NationalTeamWatch.Application;
using Microsoft.Data.Sqlite;

namespace CRS.Plugins.NationalTeam.Jobs;

/// <summary>来源限速和退避独立存入加密库，重启不能清除交易所拒绝状态。</summary>
public sealed class SourceRequestPolicy : ISourceRequestPolicy
{
    private readonly SqliteConnection db;
    public SourceRequestPolicy(string directory, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("来源策略需要32字节服务密钥。");
        Directory.CreateDirectory(directory);
        SQLitePCL.Batteries_V2.Init();
        db = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "source-policy.db"), Password = Convert.ToHexString(key), Pooling = false }.ToString());
        try
        {
            db.Open();
            using (var version = db.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(version.ExecuteScalar()) is not (0 or 1)) throw new InvalidDataException("来源策略库版本不受支持。");
            }
            using var command = db.CreateCommand();
            command.CommandText = """
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS source_policy(source TEXT PRIMARY KEY,next_utc TEXT NOT NULL,blocked_utc TEXT NOT NULL,failures INTEGER NOT NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        catch { db.Dispose(); throw; }
    }

    // 上交所多个公开域名共用额度，避免切换域名绕过拒绝；深交所使用独立额度。
    private static string Source(string host) => host switch
    {
        "www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn" => "SSE",
        "www.szse.cn" or "disc.static.szse.cn" => "SZSE",
        "www.chinacnd.com" => "ISSUER-600153",
        _ => throw new ArgumentException("不支持的来源。")
    };

    public (DateTimeOffset Next, DateTimeOffset Blocked, int Failures) Read(string host)
    {
        try{return ReadCore(host);}
        catch(SqliteException error){throw new IOException("插件来源策略读取失败。",error);}
    }
    private (DateTimeOffset Next,DateTimeOffset Blocked,int Failures) ReadCore(string host)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT next_utc,blocked_utc,failures FROM source_policy WHERE source=$s";
        command.Parameters.AddWithValue("$s", Source(host));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt32(2)) : (DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0);
    }

    /// <summary>先持久登记请求间隔，再发起网络请求；写失败时不访问官网。</summary>
    public void Reserve(string host, DateTimeOffset now)
    {
        var state = Read(host);
        Save(host, now.AddSeconds(3), state.Blocked, state.Failures);
    }

    public void Success(string host) => Save(host, Read(host).Next, DateTimeOffset.MinValue, 0);

    /// <summary>拒绝至少冷却六小时；临时故障指数退避，服务器Retry-After可以延长等待。</summary>
    public void Failure(string host, DateTimeOffset now, int status, DateTimeOffset? retryAfter = null)
    {
        var state = Read(host);
        var failures = Math.Min(state.Failures + 1, 16);
        var seconds = status is 403 or 429 ? 21600 : Math.Min(21600, 300 * Math.Pow(2, Math.Min(failures - 1, 7)));
        var until = now.AddSeconds(seconds * (1 + Random.Shared.NextDouble() * .2));
        if (retryAfter > until) until = retryAfter.Value;
        if (state.Blocked > until) until = state.Blocked;
        Save(host, state.Next, until, failures);
    }

    private void Save(string host,DateTimeOffset next,DateTimeOffset blocked,int failures)
    {
        try{SaveCore(host,next,blocked,failures);}
        catch(SqliteException error){throw new IOException("插件来源策略持久化失败。",error);}
    }
    private void SaveCore(string host, DateTimeOffset next, DateTimeOffset blocked, int failures)
    {
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO source_policy VALUES($s,$n,$b,$f) ON CONFLICT(source) DO UPDATE SET next_utc=$n,blocked_utc=$b,failures=$f";
        command.Parameters.AddWithValue("$s", Source(host));
        command.Parameters.AddWithValue("$n", next.ToString("O"));
        command.Parameters.AddWithValue("$b", blocked.ToString("O"));
        command.Parameters.AddWithValue("$f", failures);
        command.ExecuteNonQuery();
    }

    public void Dispose() => db.Dispose();
}
