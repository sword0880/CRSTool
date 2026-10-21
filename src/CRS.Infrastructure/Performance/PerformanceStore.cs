using CRS.Application.Performance;
using CRS.Domain.Performance;
using Dapper;

namespace CRS.Infrastructure;

/// <summary>绩效独立模块格式，所有原件、修订和历史快照进入同一 SQLCipher 库及加密备份。</summary>
internal sealed class PerformanceStore : IPerformanceStore
{
    private readonly VaultSession session;
    private sealed record Settings(int Version,InvestmentPortfolio[] Portfolios,ClassificationRevision[] Revisions,TransferLink[] Transfers)
    {
        public Dictionary<string,string> Aliases {get;init;}=[];
        public Dictionary<string,string> BaseCurrencies {get;init;}=[];
        public PerformanceSourceRevision[] Sources {get;init;}=[];
        public Dictionary<string,BrokerImportProvenance> ImportProvenance {get;init;}=[];
    }
    internal PerformanceStore(VaultSession session)
    {
        this.session=session;using var lease=session.Enter();using var c=session.Open();using var tx=c.BeginTransaction();
        var count=c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('performance_settings','performance_documents','performance_runs')",transaction:tx);
        if(count==0)
        {
            c.Execute("""
                CREATE TABLE performance_settings(id INTEGER PRIMARY KEY CHECK(id=1),payload BLOB NOT NULL CHECK(length(payload)<=4194304));
                CREATE TABLE performance_documents(hash TEXT PRIMARY KEY,name TEXT NOT NULL,format TEXT NOT NULL,raw BLOB NOT NULL CHECK(length(raw) BETWEEN 1 AND 26214400),data BLOB NOT NULL CHECK(length(data) BETWEEN 1 AND 67108864));
                CREATE TABLE performance_runs(id TEXT PRIMARY KEY,parent_id TEXT REFERENCES performance_runs(id),created_utc TEXT NOT NULL,payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 67108864),metadata BLOB NOT NULL CHECK(length(metadata) BETWEEN 1 AND 65536));
                """,transaction:tx);
            c.Execute("INSERT INTO performance_settings VALUES(1,@payload)",new{payload=EncodeSettings(new Settings(2,[],[],[]))},tx);
        }
        else if(count!=3)throw new CrsException("绩效存储结构不完整，不会重建或覆盖。");
        // 不借主业务版本号隐藏模块升级；字段和独立格式都必须明确匹配。
        foreach(var table in new[]{("performance_settings",new[]{"id","payload"}),("performance_documents",new[]{"hash","name","format","raw","data"}),("performance_runs",new[]{"id","parent_id","created_utc","payload","metadata"})})
            if(!c.Query<string>("SELECT name FROM pragma_table_info(@name)",new{name=table.Item1},tx).SequenceEqual(table.Item2))throw new CrsException("绩效表结构无效，不会自动修补。");
        _=ReadSettings(c,tx);tx.Commit();
    }
    private static Settings ReadSettings(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction? tx=null)
    {
        var bytes=c.ExecuteScalar<byte[]>("SELECT CASE WHEN length(payload)<=4194304 THEN payload ELSE NULL END FROM performance_settings WHERE id=1",transaction:tx)??throw new CrsException("绩效设置超限或缺失。");
        var s=PerformanceCodec.Decode<Settings>(bytes);if(s.Version is not (1 or 2))throw new CrsException("绩效设置版本不支持。");
        if(s.ImportProvenance is null||s.ImportProvenance.Count>500)throw new CrsException("绩效解析版本依据无效或超限。");
        if(s.Version==1&&s.ImportProvenance.Count!=0)throw new CrsException("旧版绩效设置不能携带新版解析依据。");
        foreach(var pair in s.ImportProvenance)
        {if(pair.Value is null||pair.Key!=pair.Value.SourceFileHash)throw new CrsException("绩效版本依据来源不一致。");pair.Value.Validate();}
        return s;
    }
    private static byte[] EncodeSettings(Settings settings)=>PerformanceCodec.Encode(settings with{Version=2});
    public PerformanceWorkspace Load()
    {
        using var lease=session.Enter();using var c=session.Open();using var tx=c.BeginTransaction(deferred:true);
        var settings=ReadSettings(c,tx);
        if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM performance_documents",transaction:tx)>500 || c.ExecuteScalar<long>("SELECT COALESCE(SUM(length(data)),0) FROM performance_documents",transaction:tx)>128L*1024*1024)throw new CrsException("绩效工作区资料超过读取限额。");
        VerifyOriginals(c,tx);
        var sourceRows=new List<PerformanceInputInfo>();
        var excluded=settings.Sources.OrderBy(s=>s.CreatedAt).GroupBy(s=>s.Hash).Where(g=>!g.Last().Included).Select(g=>g.Key).ToHashSet();
        var documents=ReadDocuments(c,tx,settings,excluded);
        foreach(var row in c.Query<(string Hash,string Name)>("SELECT hash,name FROM performance_documents ORDER BY hash",transaction:tx))sourceRows.Add(new(row.Hash,row.Name,!excluded.Contains(row.Hash)){Provenance=settings.ImportProvenance.GetValueOrDefault(row.Hash)});
        var data=Merge(documents) with {Revisions=settings.Revisions,Transfers=settings.Transfers};
        // 只修正旧版摘要占位别名，不修改原件、冻结历史或用户命名。
        if(data.Accounts.Any(a=>a.Broker=="FUTU" && a.Id.Length>=6 && a.Alias=="富途账户 "+a.Id[^6..]))
        {
            var labels=new Dictionary<string,string>();
            foreach(var row in c.Query<(string Hash,string Name)>("SELECT hash,name FROM performance_documents WHERE format='.xlsx'",transaction:tx))
            {
                if(excluded.Contains(row.Hash))continue;
                var raw=c.ExecuteScalar<byte[]>("SELECT CASE WHEN length(raw)<=26214400 THEN raw ELSE NULL END FROM performance_documents WHERE hash=@Hash",new{row.Hash},tx)??throw new CrsException("富途账户原件超限。");
                foreach(var label in PerformanceBrokerImporter.FutuAccountLabels(FrozenInput.FromBytes(row.Name,raw)))labels[label.Key]=label.Value;
            }
            data=data with{Accounts=data.Accounts.Select(a=>a.Id.Length>=6 && a.Alias=="富途账户 "+a.Id[^6..] && labels.TryGetValue(a.Id,out var label)?a with{Alias=label}:a).ToArray()};
        }
        data=data with{Accounts=data.Accounts.Select(a=>settings.Aliases.TryGetValue(a.Id,out var alias)?a with{Alias=alias}:a).ToArray()};
        data=data with{Accounts=data.Accounts.Select(a=>settings.BaseCurrencies.TryGetValue(a.Id,out var currency)?a with{BaseCurrency=currency,BaseCurrencyConfirmed=true}:a).ToArray()};
        // 列表只读取有界索引，不能每次刷新解码一百份完整交易/估值快照。
        var history=c.Query<(string Id,byte[] Metadata)>("SELECT id,CASE WHEN length(metadata)<=65536 THEN metadata ELSE NULL END FROM performance_runs ORDER BY julianday(created_utc) DESC,id DESC LIMIT 100",transaction:tx).Select(row=>
        {if(row.Metadata is null)throw new CrsException("绩效历史索引超过限额。");var h=PerformanceCodec.Decode<PerformanceHistory>(row.Metadata);if(h.Id!=row.Id)throw new CrsException("绩效索引身份不一致。");return h;}).ToArray();
        var identities=ReadAccountIdentities(c,tx);
        tx.Commit();return new(PerformanceCodec.Ordered(data),settings.Portfolios,history){Sources=sourceRows.ToArray(),AccountIdentities=identities};
    }
    private PerformanceAccountIdentity[] ReadAccountIdentities(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        var identities=new List<PerformanceAccountIdentity>();
        foreach(var row in c.Query<(string Hash,string Name,string Format)>("SELECT hash,name,format FROM performance_documents WHERE format IN ('.xml','.xlsx') ORDER BY hash",transaction:tx))
        {
            session.Lifetime.ThrowIfCancellationRequested();
            var raw=c.ExecuteScalar<byte[]>("SELECT CASE WHEN length(raw)<=26214400 THEN raw ELSE NULL END FROM performance_documents WHERE hash=@Hash",new{row.Hash},tx)??throw new CrsException("账户原件超过读取限额。");
            try{identities.AddRange(PerformanceAccountIdentityReader.Read(FrozenInput.FromBytes(row.Name,raw),row.Format));}
            finally{System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);}
        }
        return identities.GroupBy(i=>i.AccountId).Select(g=>g.First()).ToArray();
    }
    private static PerformanceData Merge(IEnumerable<PerformanceData> input)
    {
        var docs=input.ToArray();
        T[] Unique<T>(IEnumerable<T> rows,Func<T,string> key,Func<T,T,bool>? equals=null)
        {
            var result=new List<T>();foreach(var group in rows.GroupBy(key))
            {var first=group.First();if(group.Skip(1).Any(x=>equals is null?!PerformanceCodec.Encode(x).SequenceEqual(PerformanceCodec.Encode(first)):!equals(first,x)))throw new CrsException("绩效重复业务键内容冲突；请核对来源，不会自动覆盖。");result.Add(first);}return result.ToArray();
        }
        // 稳定券商业务 ID 去重；来源不同但经济字段一致时保留首份事实，原件仍各自归档。
        return new()
        {
            Accounts=docs.SelectMany(d=>d.Accounts).GroupBy(a=>a.Id).Select(g=>
            {var first=g.First();if(g.Any(a=>a.Broker!=first.Broker || a.BaseCurrency!=first.BaseCurrency))throw new CrsException("账户券商或币种冲突。");return first with{TrustedFrom=g.Min(a=>a.TrustedFrom),Margin=g.Any(a=>a.Margin)};}).ToArray(),
            Events=Unique(docs.SelectMany(d=>d.Events),e=>e.Id,(a,b)=>a with {Source=b.Source}==b),
            Valuations=Unique(docs.SelectMany(d=>d.Valuations),v=>v.Id),
            Prices=Unique(docs.SelectMany(d=>d.Prices),p=>p.Id,(a,b)=>a with{Source=b.Source}==b),Fx=Unique(docs.SelectMany(d=>d.Fx),f=>f.Id),
            Coverage=docs.SelectMany(d=>d.Coverage).ToArray()
        };
    }
    private void VerifyOriginals(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        // 对恢复后的原件逐个流式复核，不将整个原件集合读入内存，也不写明文临时文件。
        foreach(var row in c.Query<(long Id,string Hash,long Length)>("SELECT rowid,hash,length(raw) FROM performance_documents",transaction:tx))
        {
            session.Lifetime.ThrowIfCancellationRequested();
            if(row.Length is <=0 or >26214400)throw new CrsException("绩效原件大小无效。");
            using var blob=new Microsoft.Data.Sqlite.SqliteBlob(c,"performance_documents","raw",row.Id,readOnly:true);
            var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(blob));
            if(hash!=row.Hash)throw new CrsException("绩效原件摘要不一致，停止读取，请恢复可信备份。");
        }
    }
    public bool Import(PerformanceDocument document)
    {
        if(document.Hash!=PerformanceCodec.Hash(document.Raw) || document.Raw.Length is <=0 or >25*1024*1024)throw new CrsException("绩效原件摘要或大小不一致。");
        ValidateProvenance(document);
        var encoded=PerformanceCodec.Encode(document.Data);using var lease=session.Enter();using var c=session.Open();using var tx=c.BeginTransaction();
        var exists=c.ExecuteScalar<int>("SELECT COUNT(*) FROM performance_documents WHERE hash=@Hash",document,tx)>0;
        if(exists && document.Format!="YahooChartJSON")return false;
        if(!exists && (c.ExecuteScalar<int>("SELECT COUNT(*) FROM performance_documents",transaction:tx)>=500 || c.ExecuteScalar<long>("SELECT COALESCE(SUM(length(data)),0) FROM performance_documents",transaction:tx)+encoded.Length>128L*1024*1024))throw new CrsException("导入后绩效工作区会超过限额。");
        // 导入预检在同一事务中完成，冲突或重复成交现金汇总不留下半份资料。
        var currentSettings=ReadSettings(c,tx);var excluded=currentSettings.Sources.OrderBy(s=>s.CreatedAt).GroupBy(s=>s.Hash).Where(g=>!g.Last().Included).Select(g=>g.Key).ToHashSet();
        if(document.Format=="YahooChartJSON")
        {
            var symbol=document.Data.Prices.First(p=>p.Market=="INDEX").Security;
            foreach(var old in c.Query<(string Hash,byte[] Data)>("SELECT hash,data FROM performance_documents WHERE format='YahooChartJSON'",transaction:tx))
                if(old.Hash!=document.Hash && !excluded.Contains(old.Hash) && PerformanceCodec.Decode<PerformanceData>(old.Data).Prices.Any(p=>p.Market=="INDEX" && p.Security==symbol))
                {excluded.Add(old.Hash);currentSettings=currentSettings with{Sources=[..currentSettings.Sources,new(old.Hash,false,"联网更新同一指数，保留旧原响应和冻结历史。",CRS.Timing.BeijingClock.Now)]};}
            if(excluded.Remove(document.Hash))currentSettings=currentSettings with{Sources=[..currentSettings.Sources,new(document.Hash,true,"重新使用已缓存的指数响应。",CRS.Timing.BeijingClock.Now)]};
            c.Execute("UPDATE performance_settings SET payload=@payload WHERE id=1",new{payload=EncodeSettings(currentSettings)},tx);
        }
        var previous=c.Query<(string Hash,byte[] Data)>("SELECT hash,data FROM performance_documents",transaction:tx).Where(r=>!excluded.Contains(r.Hash)).Select(r=>PerformanceCodec.Decode<PerformanceData>(r.Data));
        var merged=Merge(previous.Append(document.Data));
        if(merged.Events.Any(e=>!merged.Accounts.Any(a=>a.Id==e.AccountId)) || merged.Valuations.Any(v=>!merged.Accounts.Any(a=>a.Id==v.AccountId)) || merged.Coverage.Any(v=>!merged.Accounts.Any(a=>a.Id==v.AccountId)))throw new CrsException("补录资料引用了不存在的账户，请先登记或导入账户。");
        if(merged.Accounts.Length>100 || merged.Events.Length>20000 || merged.Valuations.Length>20000 || merged.Prices.Length>50000 || merged.Fx.Length>50000)throw new CrsException("合并后的绩效资料超过集合限额。");
        if(merged.Accounts.Length>0)_=PerformanceCalculator.Calculate(merged,new(merged.Accounts.Select(a=>a.Id).ToArray(),new(2000,1,1),new(2000,1,2)));
        if(!exists)c.Execute("INSERT INTO performance_documents VALUES(@Hash,@Name,@Format,@Raw,@data)",new{document.Hash,document.Name,document.Format,document.Raw,data=encoded},tx);
        SaveProvenance(c,tx,document,currentSettings,exists);
        Capacity(c,tx);tx.Commit();return !exists;
    }
    private static List<PerformanceData> ReadDocuments(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction tx,Settings settings,HashSet<string> excluded)
    {
        var documents=new List<PerformanceData>();
        foreach(var row in c.Query<(string Hash,byte[] Data)>("SELECT hash,CASE WHEN length(data)<=67108864 THEN data ELSE NULL END FROM performance_documents ORDER BY hash",transaction:tx))
        {
            if(row.Data is null)throw new CrsException("绩效输入超限。");
            var decoded=PerformanceCodec.Decode<PerformanceData>(row.Data);
            if(settings.ImportProvenance.TryGetValue(row.Hash,out var provenance)&&provenance.DataSnapshotId!=PerformanceSnapshot.Identity(decoded))throw new CrsException("绩效版本依据与导入事实摘要不一致。");
            if(!excluded.Contains(row.Hash))documents.Add(decoded);
        }
        return documents;
    }
    private static void SaveProvenance(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction tx,PerformanceDocument document,Settings settings,bool exists)
    {
        if(exists||document.Provenance is not {} provenance)return;
        var next=settings with{ImportProvenance=new(settings.ImportProvenance){[document.Hash]=provenance}};
        var payload=EncodeSettings(next);if(payload.Length>4*1024*1024)throw new CrsException("绩效版本依据超过台账限额。");
        c.Execute("UPDATE performance_settings SET payload=@payload WHERE id=1",new{payload},tx);
    }
    private static void ValidateProvenance(PerformanceDocument document)
    {
        if(document.Provenance is not {} p)return;
        p.Validate();
        if(p.SourceFileHash!=document.Hash||p.DataSnapshotId!=PerformanceSnapshot.Identity(document.Data)
            ||(document.Format,p.Broker) is not ((".xml","IBKR") or (".xlsx","FUTU")))
            throw new CrsException("导入事实、原件与解析版本依据不匹配。");
    }
    private void Update(Func<Settings,Settings> action)
    {using var lease=session.Enter();using var c=session.Open();using var tx=c.BeginTransaction();var next=EncodeSettings(action(ReadSettings(c,tx)));if(next.Length>4*1024*1024)throw new CrsException("绩效修订台账超过限额。");c.Execute("UPDATE performance_settings SET payload=@next WHERE id=1",new{next},tx);Capacity(c,tx);tx.Commit();}
    public void SavePortfolio(InvestmentPortfolio portfolio)
    {
        var data=Load().Data;
        if(string.IsNullOrWhiteSpace(portfolio.Id) || string.IsNullOrWhiteSpace(portfolio.Name) || portfolio.Name.Length>100 || portfolio.AccountIds.Length is <1 or >100 || portfolio.AccountIds.Distinct().Count()!=portfolio.AccountIds.Length || portfolio.AccountIds.Any(id=>!data.Accounts.Any(a=>a.Id==id)) || portfolio.Currency is not ("CNY" or "USD" or "HKD"))throw new CrsException("组合名称、账户范围或币种无效。");
        Update(s=>s with {Portfolios=s.Portfolios.Where(p=>p.Id!=portfolio.Id).Append(portfolio).ToArray()});
    }
    public void Revise(ClassificationRevision revision)
    {var original=Load().Data.Events.SingleOrDefault(e=>e.Id==revision.EventId)??throw new CrsException("待修订事件不存在。");if(original.Kind is InvestmentEventKind.Unsupported or InvestmentEventKind.Buy or InvestmentEventKind.Sell or InvestmentEventKind.Split or InvestmentEventKind.FxExchange or InvestmentEventKind.SecurityTransfer && revision.Kind!=original.Kind)throw new CrsException("证券、换汇或复杂事件不能仅改分类放行；请排除该错误来源并补录有证据的完整资料。");PerformanceClassifications.ValidateDirection(original with{Kind=revision.Kind});Update(s=>s with {Revisions=s.Revisions.Append(revision).ToArray()});}
    public void RenameAccount(string id,string alias)
    {if(!Load().Data.Accounts.Any(a=>a.Id==id))throw new CrsException("账户不存在。");Update(s=>s with{Aliases=new(s.Aliases){[id]=alias}});}
    public void ConfirmBaseCurrency(string id,string currency)
    {if(!Load().Data.Accounts.Any(a=>a.Id==id))throw new CrsException("账户不存在。");Update(s=>s with{BaseCurrencies=new(s.BaseCurrencies){[id]=currency}});}
    public void SourceRevision(PerformanceSourceRevision revision)
    {
        using var lease=session.Enter();using var c=session.Open();using var tx=c.BeginTransaction();
        if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM performance_documents WHERE hash=@Hash",revision,tx)!=1)throw new CrsException("来源不存在。");
        var settings=ReadSettings(c,tx);var next=settings with{Sources=settings.Sources.Append(revision).ToArray()};
        var excluded=next.Sources.OrderBy(s=>s.CreatedAt).GroupBy(s=>s.Hash).Where(g=>!g.Last().Included).Select(g=>g.Key).ToHashSet();
        var active=Merge(c.Query<(string Hash,byte[] Data)>("SELECT hash,data FROM performance_documents",transaction:tx).Where(r=>!excluded.Contains(r.Hash)).Select(r=>PerformanceCodec.Decode<PerformanceData>(r.Data)));
        // 排除账户原件不得把其他有效补录变成悬空资料；先同时处理依赖来源。
        if(active.Events.Any(e=>!active.Accounts.Any(a=>a.Id==e.AccountId)) || active.Valuations.Any(v=>!active.Accounts.Any(a=>a.Id==v.AccountId)) || active.Coverage.Any(v=>!active.Accounts.Any(a=>a.Id==v.AccountId)))throw new CrsException("该来源仍被其他补录引用，请先处理依赖来源，再排除账户资料。");
        var payload=EncodeSettings(next);if(payload.Length>4*1024*1024)throw new CrsException("绩效修订超过台账限额。");c.Execute("UPDATE performance_settings SET payload=@payload WHERE id=1",new{payload},tx);Capacity(c,tx);tx.Commit();
    }
    public void Transfer(TransferLink link)
    {
        var data=Load().Data;var effective=PerformanceClassifications.Apply(data);var outgoing=effective.SingleOrDefault(e=>e.Id==link.OutEventId);var incoming=effective.SingleOrDefault(e=>e.Id==link.InEventId);
        if(outgoing is null || incoming is null || outgoing.AccountId==incoming.AccountId || outgoing.Amount>=0 || incoming.Amount<=0 || outgoing.Kind!=incoming.Kind || outgoing.Kind is not (InvestmentEventKind.CashTransfer or InvestmentEventKind.SecurityTransfer) || (outgoing.Kind==InvestmentEventKind.SecurityTransfer && (outgoing.Security!=incoming.Security || outgoing.Market!=incoming.Market || Math.Abs(outgoing.Quantity)!=Math.Abs(incoming.Quantity))))throw new CrsException("关联必须是不同账户的同类转入/转出，证券和数量一致。");
        Update(s=>s.Transfers.Any(l=>l.OutEventId==link.OutEventId || l.InEventId==link.InEventId)?throw new CrsException("转账一侧已被匹配，不能重复关联。"):s with {Transfers=s.Transfers.Append(link).ToArray()});
    }
    public void SaveRun(PerformanceRun run)
    {
        PerformanceEvidence.Verify(run);
        var payload=PerformanceCodec.Encode(run);using var lease=session.Enter();using var c=session.Open();using var tx=c.BeginTransaction();
        c.Execute("UPDATE performance_settings SET payload=@settings WHERE id=1",new{settings=EncodeSettings(ReadSettings(c,tx))},tx);
        // 完整范围仍保存在冻结输入；列表摘要有界，避免百账户长别名超出解码字符串限额。
        var scope=string.Join("、",run.Result.Accounts.Select(a=>a.AccountAlias));if(scope.Length>4000)scope=scope[..3900]+$"…（共 {run.Result.Accounts.Length} 个账户，完整范围见原结果）";
        var metadata=PerformanceCodec.Encode(new PerformanceHistory(run.Id,run.CreatedAt,run.Result.EffectiveFrom,run.Result.Request.To,run.Result.Request.Currency,scope,run.InputHash));
        if(metadata.Length>65536)throw new CrsException("绩效历史索引超过 64 KiB。");
        c.Execute("INSERT INTO performance_runs VALUES(@Id,@ParentId,@created,@payload,@metadata)",new{run.Id,run.ParentId,created=run.CreatedAt.ToString("O"),payload,metadata},tx);Capacity(c,tx);tx.Commit();
    }
    public PerformanceRun OpenRun(string id)
    {using var lease=session.Enter();using var c=session.Open();return Open(c,id,null);}
    private static PerformanceRun Open(Microsoft.Data.Sqlite.SqliteConnection c,string id,Microsoft.Data.Sqlite.SqliteTransaction? tx)
    {
        var bytes=c.ExecuteScalar<byte[]>("SELECT CASE WHEN length(payload)<=67108864 THEN payload ELSE NULL END FROM performance_runs WHERE id=@id",new{id},tx)??throw new CrsException("绩效历史不存在或超限。");
        var run=PerformanceCodec.Decode<PerformanceRun>(bytes);
        if(run.Id!=id || run.InputHash!=PerformanceCodec.Hash(PerformanceCodec.Encode(PerformanceCodec.Ordered(run.Input))))throw new CrsException("绩效历史输入完整性校验失败。");PerformanceEvidence.Verify(run);return run;
    }
    private void Capacity(Microsoft.Data.Sqlite.SqliteConnection c,Microsoft.Data.Sqlite.SqliteTransaction tx)
    {session.Lifetime.ThrowIfCancellationRequested();if(c.ExecuteScalar<long>("PRAGMA page_count",transaction:tx)*c.ExecuteScalar<long>("PRAGMA page_size",transaction:tx)>ChunkedVaultBackup.MaxDatabaseBytes)throw new CrsException("写入后超过 2 GiB 备份容量，本次绩效修改已回滚。");}
}
