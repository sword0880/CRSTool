using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using Microsoft.Data.Sqlite;

/// <summary>同一合成输入对照旧发行与新存储；不打开正式库、审核真实持仓或发送通知。</summary>
internal static class EtfEvidenceOwnershipChecks
{
    private static readonly EtfKey Key=new("SSE","510300");
    private static int passed;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);passed++;Console.WriteLine("PASS ETF证据归属："+message);}
    public static void Run(string root,string? legacyAssembly=null)
    {
        root=Path.GetFullPath(root);if(Directory.Exists(root))throw new IOException("验收目录已存在。");Directory.CreateDirectory(root);passed=0;
        using var legacy=legacyAssembly is null?null:new Legacy(legacyAssembly);
        IMarketRepository Open(string directory,bool old)=>old?legacy!.Open(directory):TestMarketData.Open(Path.Combine(directory,"data"),new(Path.Combine(directory,"raw")),timeProvider:new ExitAlertChecks.ManualClock());
        var currentRoot=Path.Combine(root,"current");
        var current=Exercise(currentRoot,false,Open);
        if(legacy is not null)
        {
            var oldRoot=Path.Combine(root,"legacy");var previous=Exercise(oldRoot,true,Open);
            Check(previous==current,"旧发行与新存储的表结构、登记、审核、命令及故障恢复结果逐项相同");
            using(var reopened=Open(oldRoot,false))
                Check(State(reopened,oldRoot)==previous,"新代码直接重开旧发行所建普通库，原件、审核、命令和首见保持");
        }
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,LegacyAssembly=legacyAssembly,Installed=false,Network="none",CurrentState=JsonDocument.Parse(current).RootElement},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"ETF_EVIDENCE_OWNERSHIP passed={passed};legacyCompared={legacy is not null};installed=false;network=none");
    }
    private static string Exercise(string root,bool old,Func<string,bool,IMarketRepository> open)
    {
        using var repo=open(root,old);var label=old?"旧发行":"新存储";
        var legacyId=Guid.Parse("11111111-1111-4111-8111-111111111111");var isolatedId=Guid.Parse("22222222-2222-4222-8222-222222222222");
        var legacyDocument=new AlertDocumentRequest(Key.Market,Key.Ticker,"https://www.sse.com.cn/ownership-legacy.pdf");
        var document=new AlertDocumentRequest(Key.Market,Key.Ticker,"https://www.sse.com.cn/ownership-isolated.pdf");
        repo.RegisterAlertDocument(legacyDocument,"%PDF-synthetic-legacy-ownership"u8.ToArray(),legacyId);
        repo.RegisterEtfDocument(document,"%PDF-synthetic-isolated-ownership"u8.ToArray(),isolatedId);
        Check(repo.AlertDocuments(Key).Count==2&&repo.EtfDocumentCommandComplete(isolatedId,document),label+"：新旧命令入口共用登记身份，独立命令绑定可读");
        var before=Counts(root);
        repo.RegisterEtfDocument(document,"%PDF-replay-must-not-replace"u8.ToArray(),isolatedId);
        Check(Counts(root)==before,label+"：已提交相同ID重放不新增来源或原件");
        var rejected=false;
        try{repo.RegisterEtfDocument(document with{Url="https://www.sse.com.cn/changed.pdf"},"%PDF-other"u8.ToArray(),isolatedId);}catch(InvalidDataException){rejected=true;}
        Check(rejected&&Counts(root)==before,label+"：同ID换原件失败关闭，旧事实保留");
        var registered=repo.AlertDocuments(Key).Single(x=>x.Url==document.Url);
        var metadata=new AlertMetadataReview(registered.Id,"synthetic-fund","synthetic-manager","CSI300",new(2026,1,1),new(2026,9,1),"synthetic-reviewer",true);
        repo.ReviewAlertMetadata(metadata,Guid.Parse("33333333-3333-4333-8333-333333333333"));
        var holding=new NamedEtfHoldingReview(registered.Id,"synthetic-legal-a","合成登记法人甲","合成独立账户甲",new(2026,8,1),new(2026,8,28,18,0,0,TimeSpan.FromHours(8)),950,"合成第10页","synthetic-reviewer",true,true);
        repo.ReviewNamedEtfHolding(holding,Guid.Parse("44444444-4444-4444-8444-444444444444"));
        Check(repo.AlertMetadata().Single().OriginalConfirmed&&repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,label+"：原件审核与独立所有权要求保持");
        var failedId=Guid.Parse("55555555-5555-4555-8555-555555555555");
        var failedDocument=document with{Url="https://www.sse.com.cn/ownership-rollback.pdf"};before=Counts(root);
        Sql(root,"CREATE TRIGGER synthetic_document_abort BEFORE INSERT ON nt_alert_pending BEGIN SELECT RAISE(ABORT,'synthetic document rollback'); END;");
        var rolledBack=false;
        try{repo.RegisterEtfDocument(failedDocument,"%PDF-synthetic-rollback"u8.ToArray(),failedId);}catch(SqliteException){rolledBack=true;}
        finally{Sql(root,"DROP TRIGGER synthetic_document_abort;");}
        Check(rolledBack&&Counts(root)==before&&!repo.EtfDocumentCommandComplete(failedId,failedDocument),label+"：待评价标记失败时原件、来源与命令同事务回滚");
        repo.RegisterEtfDocument(failedDocument,"%PDF-synthetic-rollback"u8.ToArray(),failedId);
        Check(repo.EtfDocumentCommandComplete(failedId,failedDocument)&&repo.AlertDocuments(Key).Count==3,label+"：故障解除后原ID完整重试");
        Sql(root,"UPDATE source_fetch SET status='ParseFailed' WHERE fetch_id IN(SELECT fetch_id FROM nt_alert_document WHERE url='https://www.sse.com.cn/ownership-isolated.pdf');");
        Check(!repo.AlertMetadata().Single().OriginalConfirmed&&!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,label+"：来源失效不能继承旧审核");
        var contentRejected=false;try{repo.EtfHolderDocument(Key,registered.Id);}catch(InvalidDataException){contentRejected=true;}
        Check(contentRejected,label+"：候选原件读取也沿用来源核验门槛");
        Sql(root,"UPDATE source_fetch SET status='RawSaved';");
        Check(repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,label+"：恢复来源后按当前原件重新核验，不写新审核");
        Check(repo.AlertEvents(new()).Count==0&&repo.AlertOutbox().Count==0,label+"：资料登记与审核不会夹带评价或投递");
        return State(repo,root);
    }
    private static string State(IMarketRepository repository,string root)=>JsonSerializer.Serialize(new{
        Schema=Rows(root,"SELECT type,name,sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type,name"),
        Documents=repository.AlertDocuments(Key).OrderBy(x=>x.Id),Metadata=repository.AlertMetadata(),Holdings=repository.NamedEtfHoldings(Key),
        Commands=Rows(root,"SELECT request,operation,completed_utc FROM worker_business_command ORDER BY request"),Counts=Counts(root),
        Originals=Directory.GetFiles(Path.Combine(root,"raw"),"*.raw",SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal)
    });
    private static string Counts(string root)=>Rows(root,"SELECT (SELECT count(*) FROM source_fetch),(SELECT count(*) FROM nt_alert_document),(SELECT count(*) FROM nt_alert_metadata),(SELECT count(*) FROM nt_alert_metadata_review),(SELECT count(*) FROM nt_named_etf_holding),(SELECT count(*) FROM nt_alert_pending),(SELECT count(*) FROM worker_business_command)");
    private static SqliteConnection Database(string root){var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"data","market.db"),Pooling=false}.ToString());db.Open();return db;}
    private static void Sql(string root,string text){using var db=Database(root);using var command=db.CreateCommand();command.CommandText=text;command.ExecuteNonQuery();}
    private static string Rows(string root,string sql)
    {
        using var db=Database(root);using var command=db.CreateCommand();command.CommandText=sql;using var reader=command.ExecuteReader();var rows=new List<object?[]>();
        while(reader.Read()){var row=new object[reader.FieldCount];reader.GetValues(row);rows.Add(row.Select(x=>x is DBNull?null:x).ToArray());}
        return JsonSerializer.Serialize(rows);
    }
    private sealed class Legacy:AssemblyLoadContext,IDisposable
    {
        private readonly Type repository,raw;
        public Legacy(string path):base(isCollectible:true)
        {var assembly=LoadFromAssemblyPath(Path.GetFullPath(path));repository=assembly.GetType(typeof(MarketRepository).FullName!,true)!;raw=assembly.GetType(typeof(RawEvidenceStore).FullName!,true)!;}
        protected override Assembly? Load(AssemblyName name)=>Default.LoadFromAssemblyName(name);
        public IMarketRepository Open(string root)
        {
            var evidence=Activator.CreateInstance(raw,[Path.Combine(root,"raw"),null,null]);
            return (IMarketRepository)Activator.CreateInstance(repository,[Path.Combine(root,"data"),evidence,null,TestMarketData.Key.ToArray(),new ExitAlertChecks.ManualClock(),null])!;
        }
        public void Dispose()=>Unload();
    }
}
