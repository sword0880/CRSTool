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
        Console.WriteLine($"联合导入事务验证通过：{passed}项。");
    }
    private sealed class Rates:IExchangeRateProvider
    {public string Fingerprint=>"synthetic";public AppliedRate Get(int year,string currency)=>new(year,currency,1,"synthetic","","",null,null,false,true);}
}
