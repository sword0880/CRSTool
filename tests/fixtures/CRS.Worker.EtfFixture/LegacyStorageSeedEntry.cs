using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Worker.Abstractions;
using Microsoft.Data.Sqlite;

namespace CRS.Worker.EtfFixture;

/// <summary>仅在独立LocalService测试树创建旧加密资料；不进入正式发行，也不导出任何密钥。</summary>
public sealed class LegacyStorageSeedEntry:IContextualBackgroundPluginEntry
{
    public IBackgroundPluginSession CreateSession()=>throw new InvalidOperationException();
    public IBackgroundPluginSession CreateSession(IBackgroundHostContext context)=>new SeedSession(context);
    private sealed class SeedSession(IBackgroundHostContext context):IBackgroundPluginSession
    {
        public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>new NationalTeamBackgroundSession(context).DescribeJobs().Select(x=>x with{EnabledByDefault=false,RunOnStartup=false}).ToArray();
        public ValueTask StartAsync(CancellationToken token)
        {
            var metadata=Path.Combine(context.DataDirectory,"migration-fixture-seed.json");if(File.Exists(metadata))return ValueTask.CompletedTask;
            var key=context.OpenDataKey();
            try
            {
                token.ThrowIfCancellationRequested();var plain=Path.Combine(context.DataDirectory,"migration-fixture-seed");var target=Path.Combine(context.DataDirectory,"data");Directory.CreateDirectory(target);
                var raw=new RawEvidenceStore(Path.Combine(context.DataDirectory,"raw"));var hash=raw.Save("LocalService synthetic migration fact"u8.ToArray());string market,ticker;int count;
                using(var repository=new MarketRepository(plain,raw,approvalAuditKey:key))
                {
                    var rows=repository.Configuration();var first=rows.OrderBy(x=>x.Ticker,StringComparer.Ordinal).First();market=first.Market;ticker=first.Ticker;count=rows.Count;
                    var day=new DateOnly(2026,9,1);var fetch=repository.RecordFetch(hash,"https://www.sse.com.cn/synthetic-local-service");repository.ImportCalendar(fetch,[(market,day)],token);repository.ImportShares(fetch,[new(new(market,ticker),day,123,"份")],token);
                }
                using(var db=Open(Path.Combine(plain,"market.db")))Execute(db,"UPDATE watchlist_membership SET active=0 WHERE ticker NOT IN (SELECT ticker FROM watchlist_membership ORDER BY ticker LIMIT 2)");
                using(var policy=new SourceRequestPolicy(plain))policy.Failure("www.sse.com.cn",DateTimeOffset.UtcNow,403);
                Encrypt(Path.Combine(plain,"market.db"),Path.Combine(target,"market.db"),key,2);Encrypt(Path.Combine(plain,"source-policy.db"),Path.Combine(target,"source-policy.db"),key,1);
                File.WriteAllText(metadata,JsonSerializer.Serialize(new{ObservedMarket=market,ObservedTicker=ticker,RawSha256=hash,ConfigurationCount=count,ActiveCount=2}));
                return ValueTask.CompletedTask;
            }
            finally{CryptographicOperations.ZeroMemory(key);}
        }
        public Task<BackgroundJobResult> ExecuteAsync(string jobId,CancellationToken token)=>throw new NotSupportedException("合成旧库入口不执行任务。");
        public ValueTask StopAsync(CancellationToken token)=>ValueTask.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private static SqliteConnection Open(string path){SQLitePCL.Batteries_V2.Init();var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();return db;}
    private static void Execute(SqliteConnection db,string sql){using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private static void Encrypt(string source,string target,byte[] key,int version)
    {
        using var db=Open(source);using var command=db.CreateCommand();command.CommandText="ATTACH DATABASE $target AS legacy KEY '"+Convert.ToHexString(key)+"'";command.Parameters.AddWithValue("$target",target);command.ExecuteNonQuery();command.CommandText="SELECT sqlcipher_export('legacy')";command.ExecuteScalar();Execute(db,"PRAGMA legacy.user_version="+version+"; DETACH DATABASE legacy;");
    }
}
