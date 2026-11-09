using CRS.Application;
using CRS.Application.Abstractions;
using CRS.Domain;
using CRS.Infrastructure;
using Dapper;

/// <summary>真实SQLCipher事务验证；所有原件、触发器和金额都属于随机隔离目录。</summary>
internal static class CalculationCommitChecks
{
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var passed=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS 联合导入："+label);}
        using var vault=new VaultManager();await vault.CreateAsync(Path.Combine(root,"vault"),"Synthetic-Atomic-Import-2026");await TestStores.Enroll(vault);
        _=vault.CreatePerformanceUseCases(false);
        var operations=new DesktopOperations(vault.Repository,"",vault);
        var first=Write("first.xml","FIRST");var second=Write("second.xml","SECOND");
        string Write(string file,string account)
        {
            var path=Path.Combine(root,file);
            File.WriteAllText(path,$"<FlexQueryResponse><FlexStatements><FlexStatement accountId=\"{account}\" fromDate=\"20250101\" toDate=\"20251231\"><Trades/><CashTransactions/><OpenPositions/></FlexStatement></FlexStatements></FlexQueryResponse>");return path;
        }
        void Counts(int tax,int performance,string label)
        {using var c=vault.OpenForTest();Check(c.ExecuteScalar<int>("SELECT count(*) FROM calculations")==tax&&c.ExecuteScalar<int>("SELECT count(*) FROM performance_documents")==performance,label);}
        using(var abandoned=operations.BeginCalculation(new Rates(),"IBKR",2025))
        {_=abandoned.Calculator.Calculate([first],2025,true,true);Counts(0,0,"解析及计算完成但未提交时，两个模块都没有业务写入");}
        var broken=Path.Combine(root,"broken.xml");File.WriteAllText(broken,"not xml");
        try
        {using var batch=operations.BeginCalculation(new Rates(),"IBKR",2025);_=batch.Calculator.Calculate([first,broken],2025,true,true);throw new Exception("未拒绝损坏第二份原件");}
        catch(CrsException){Counts(0,0,"首份准备成功、第二份解析失败时，不残留绩效数据");}
        try
        {using var batch=operations.BeginCalculation(new Rates(),"FUTU",2025);_=batch.Calculator.Calculate(["tests/fixtures/futu_performance.xlsx"],2025,true,true);throw new Exception("未拒绝缺少收入主表");}
        catch(CrsException){Counts(0,0,"富途交易已解析但收入主表缺失，两个模块保持原状");}
        using(var canceled=operations.BeginCalculation(new Rates(),"IBKR",2025))
        {
            var result=canceled.Calculator.Calculate([first],2025,true,true);using var token=new CancellationTokenSource();token.Cancel();
            try{canceled.Commit(result,token.Token);throw new Exception("取消仍提交");}catch(OperationCanceledException){}
            Counts(0,0,"准备完成后取消不会提交任何业务数据");
        }
        // 在绩效插入已经发生后故意使税务INSERT失败，直接检查同一事务的回滚效果。
        using(var c=vault.OpenForTest())c.Execute("CREATE TRIGGER reject_tax BEFORE INSERT ON calculations BEGIN SELECT RAISE(ABORT,'synthetic'); END;");
        try
        {using var failed=operations.BeginCalculation(new Rates(),"IBKR",2025);var result=failed.Calculator.Calculate([first],2025,true,true);failed.Commit(result,default);throw new Exception("注入失败未生效");}
        catch(Microsoft.Data.Sqlite.SqliteException){Counts(0,0,"税务保存失败会回滚之前的绩效原件和版本记录");}
        using(var c=vault.OpenForTest())c.Execute("DROP TRIGGER reject_tax;");
        using(var success=operations.BeginCalculation(new Rates(),"IBKR",2025))
        {var result=success.Calculator.Calculate([first],2025,true,true);success.Commit(result,default);Counts(1,1,"单次提交同时保存税务和同源绩效");}
        using(var repeat=operations.BeginCalculation(new Rates(),"IBKR",2025))
        {var result=repeat.Calculator.Calculate([first],2025,true,true);repeat.Commit(result,default);Counts(2,1,"再次计算可追加税务历史，绩效原件保持幂等");}
        // 第二份准备也成功后锁定会话，旧任务不得借重新解锁的新会话继续提交。
        using(var locked=operations.BeginCalculation(new Rates(),"IBKR",2025))
        {
            var result=locked.Calculator.Calculate([second],2025,true,true);vault.Lock();
            try{locked.Commit(result,default);throw new Exception("锁定后仍提交");}catch(CrsException){Check(true,"锁定后的旧准备会话拒绝提交");}
        }
        await vault.UnlockAsync(Path.Combine(root,"vault"),"Synthetic-Atomic-Import-2026");await TestStores.Authenticate(vault);
        Counts(2,1,"重新解锁后确认锁定期间没有部分写入");
        await CheckImportHistory(root,vault,first,second,Check);
        Console.WriteLine($"联合导入事务验证通过：{passed}项。");
    }

    private static async Task CheckImportHistory(string root, VaultManager vault, string first, string second, Action<bool,string> check)
    {
        // 将已有导入留在 v2 结构，检验实际解锁时重建索引与升级前备份。
        using (var c = vault.OpenForTest()) c.Execute("DROP TABLE calculation_import_history; PRAGMA user_version=2;");
        vault.Lock();
        var directory = Path.Combine(root, "vault");
        await vault.UnlockAsync(directory, "Synthetic-Atomic-Import-2026");
        await TestStores.Authenticate(vault);
        check(Directory.GetFiles(directory,"schema-v2-*.crsbak").Length==1
            && vault.Repository.QueryHistory(1,1).Rows.Single().ActiveCopies==2,"旧库升级保留备份并跨页识别已有重复组");

        File.WriteAllText(Path.Combine(root,"exchange_rate.json"),"""{"2025":{"CNY":1},"2026":{"CNY":1}}""");
        var workflow = new DesktopWorkflow(new DesktopOperations(vault.Repository, root, vault));
        var request = new DesktopCalculationRequest([first],2025,"IBKR",true,true,false,null,true);
        var initial = (await workflow.CalculateAsync(request,default)).Result;
        var renamed = Path.Combine(root,"renamed.xml");
        File.Copy(first,renamed);
        var renamedRequest = request with { Files=[renamed] };
        ImportReview? review = null;
        Task<ImportDecision> Answer(ImportReview candidate, ImportDecision decision)
        {
            review=candidate;
            return Task.FromResult(decision);
        }
        var count = vault.Repository.History().Count;
        var opened = await workflow.ImportAsync(renamedRequest,(m,_)=>Answer(m,ImportDecision.OpenExisting),default);
        check(review is { Kind: ImportMatchKind.Identical } && review.Files.SequenceEqual(["renamed.xml"])
            && opened?.Result.SnapshotId==initial.SnapshotId && vault.Repository.History().Count==count,
            "改名副本打开原结果且不新增任务");

        review = null;
        var changed = await workflow.ImportAsync(request with { Files=[first,second] },(m,_)=>Answer(m,ImportDecision.Cancel),default);
        check(changed is null && review is { Kind: ImportMatchKind.SharedFiles }
            && review.Differences.Contains("报表组合或用途") && vault.Repository.History().Count==count,
            "部分重叠指出新增报表，取消无写入");
        var multiYear = Path.Combine(root,"multi-year.xml");
        File.WriteAllText(multiYear,File.ReadAllText(first).Replace("20251231","20261231"));
        _ = await workflow.CalculateAsync(request with { Files=[multiYear] },default);
        count++;
        review = null;
        _ = await workflow.ImportAsync(request with { Files=[multiYear], Year=2026 },(m,_)=>Answer(m,ImportDecision.Cancel),default);
        check(review is { Kind: ImportMatchKind.SharedFiles } && review.Differences.Contains("核算年度"),"跨年复用提示年度变化");

        var revised = Path.Combine(root,"quarter.xml");
        File.WriteAllText(revised,File.ReadAllText(first).Replace("20251231","20250331"));
        review = null;
        _ = await workflow.ImportAsync(request with { Files=[revised] },(m,_)=>Answer(m,ImportDecision.Cancel),default);
        check(review is { Kind: ImportMatchKind.OverlappingPeriod } && review.Files.SequenceEqual(["quarter.xml"]),
            "不同文件的同账户同类报表按期间重叠提示");

        var saved = (await workflow.ImportAsync(renamedRequest,(m,_)=>Answer(m,ImportDecision.SaveNew),default))!.Result;
        check(saved.SnapshotId!=initial.SnapshotId && vault.Repository.History().Count==count+1,
            "用户明确选择时允许相同资料另存");
        using (var racing = new DesktopOperations(vault.Repository, root, vault).BeginCalculation(new ExchangeRates(root),"IBKR",2025))
        {
            var duplicate = racing.Calculator.Calculate([first],2025,true,true,saveCanonicalSnapshot:true);
            try { racing.Commit(duplicate,default,false); throw new Exception("未阻止并发提交的重复结果"); }
            catch (CrsException) { check(vault.Repository.History().Count==count+1,"提交时拦截新出现的同批次并发重复"); }
        }

        await workflow.SaveReviewAsync(initial.SnapshotId,"合成复核","保留归档任务复核依据");
        await workflow.SetHistoryArchivedAsync(initial.SnapshotId,true);
        var archived = await workflow.QueryArchivedHistoryAsync(1,50);
        check(archived.Rows.Single().Id==initial.SnapshotId && archived.Rows[0].IsArchived
            && vault.Repository.History().All(t=>t.Id!=initial.SnapshotId)
            && vault.Repository.Load(initial.SnapshotId).EvidenceSeal==initial.EvidenceSeal
            && vault.Repository.Reviews(initial.SnapshotId).Count==1,"归档保留冻结结果和复核记录，并从日常列表移出");
        var replayed = await workflow.ReplayAsync(initial.SnapshotId,default);
        check(replayed.ParentSnapshotId==initial.SnapshotId,"归档任务仍可用作复算父任务");
        try { await workflow.SetHistoryArchivedAsync(saved.SnapshotId,true); throw new Exception("归档了最后一个在用副本"); }
        catch (CrsException) { check(vault.Repository.History().Any(t=>t.Id==saved.SnapshotId),"归档事务保留最后一个在用副本"); }
        await workflow.SetHistoryArchivedAsync(initial.SnapshotId,false);
        check((await workflow.QueryArchivedHistoryAsync(1,50)).TotalCount==0
            && vault.Repository.History().Any(t=>t.Id==initial.SnapshotId),"恢复后重新出现在日常列表");
    }
    private sealed class Rates:IExchangeRateProvider
    {public string Fingerprint=>"synthetic";public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","","",null,null,false,true);}
}
