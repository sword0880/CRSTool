using System.Text.Json;
using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;

internal static class ForeignCreditChecks
{
    /// <summary>验证国家隔离、所得事实、凭证去重、派生持久化与凭证正文隐私。</summary>
    public static async Task RunAsync(string root)
    {
        var directory = Path.Combine(root, "credit"); Directory.CreateDirectory(directory);
        const string sentinel = "SENSITIVE_PROOF_BODY_DO_NOT_STORE";
        File.WriteAllText(Path.Combine(directory, "proof.txt"), sentinel);
        var path = Path.Combine(directory, "credit.json");
        File.WriteAllText(path, """
        {"Version":1,"Year":2025,"Income":[
          {"Broker":"IBKR","Account":"DEMO","Country":"US","Category":"Dividend","Currency":"USD","Amount":100},
          {"Broker":"IBKR","Account":"DEMO","Country":"HK","Category":"Dividend","Currency":"USD","Amount":100}],
         "Payments":[{"Broker":"IBKR","Account":"DEMO","Country":"US","Category":"Dividend","Currency":"USD","Amount":30,
           "Authority":"Synthetic Authority","Reference":"DEMO-001","Exclusion":"","Proofs":[{"Role":"TaxReceipt","Path":"proof.txt"}]}]}
        """);
        var document = ForeignCreditFiles.Read(path);
        var source = new CalculationResult { Year=2025,SnapshotId="CREDIT-SOURCE",CoveredAccounts=["DEMO"],
            Summary=new(200,0,0,40,0,30,10),
            TaxInputs=new(1,TaxEngine.PolicyVersion,[new("DEMO",2025,"USD",200,0)],[new("DEMO",2025,"USD",30)]),
            Rates=[new(2025,"USD",1,"Synthetic","","",null,null,false,true)] };
        var assessed = ForeignCreditReview.Assess(document,2025,[source],source.Rates);
        if (assessed.CreditCny != 20 || assessed.Countries.Single(c=>c.Country=="US").ExcessCny != 10
            || assessed.Countries.Single(c=>c.Country=="HK").CreditCny != 0) throw new Exception("国家限额被其他国家收入冲抵。");
        void Reject(ForeignCreditEvidence invalid)
        {
            try { ForeignCreditReview.Assess(invalid,2025,[source],source.Rates); }
            catch (CrsException) { return; }
            throw new Exception("无效抵免明细被接受。");
        }
        Reject(document with { Year=2024 });
        Reject(document with { Income=[document.Income[0] with { Amount=101 },document.Income[1]] });
        Reject(document with { Income=[document.Income[0] with { Account="OTHER" },document.Income[1]] });
        Reject(document with { Payments=[document.Payments[0] with { Amount=31 }] });
        Reject(document with { Payments=[document.Payments[0] with { Proofs=[] }] });
        Reject(document with { Payments=[document.Payments[0] with { Amount=15 },document.Payments[0] with { Amount=15,Reference="DEMO-002" }] });
        Reject(document with { Payments=[document.Payments[0] with { Category="Interest" }] });
        var excluded = ForeignCreditReview.Assess(document with { Payments=[document.Payments[0] with { Exclusion="Refunded" }] },2025,[source],source.Rates);
        if (excluded.CreditCny != 0 || excluded.Countries.Single(c=>c.Country=="US").ExcludedCny != 30) throw new Exception("已退税款仍被抵免。");
        var store = new LocalStore(Path.Combine(directory,"db")); store.Save(source);
        var before = JsonSerializer.Serialize(store.Load(source.SnapshotId));
        var workflow = new DesktopWorkflow(new DesktopOperations(store,Path.Combine(directory,"no-current-rates")));
        var result = await workflow.ReviewForeignCreditAsync(source.SnapshotId,path,CancellationToken.None);
        var loaded = store.Load(result.SnapshotId);
        var frozen = JsonSerializer.Serialize(loaded);
        if (loaded.Summary.ForeignCredit != 20 || loaded.Summary.SupplementTax != 20 || loaded.EstimatedTopUpCny is not null
            || loaded.IsReplayable || loaded.Complete || loaded.ParentSnapshotId != source.SnapshotId
            || frozen.Contains(sentinel) || frozen.Contains(directory.Replace("\\","\\\\"))
            || loaded.ForeignCreditAssessment!.Evidence.Payments[0].Proofs[0].Sha256.Length != 64
            || JsonSerializer.Serialize(store.Load(source.SnapshotId)) != before)
            throw new Exception("复核结果、原任务保护或凭证隐私失败。");
        var excel = Path.Combine(directory,"credit.xlsx"); await workflow.ExportAsync(excel,loaded,"");
        if (ExcelReports.ReadSheet(excel,"抵免国家限额").Count != 3 || ExcelReports.ReadSheet(excel,"抵免凭证关系").Count != 2)
            throw new Exception("抵免证据链未导出。");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var count = store.QueryHistory(1,50).TotalCount;
        try { await workflow.ReviewForeignCreditAsync(source.SnapshotId,path,cancelled.Token); throw new Exception("取消抵免仍保存。"); }
        catch (OperationCanceledException) { }
        if (store.QueryHistory(1,50).TotalCount != count) throw new Exception("取消抵免留下任务。");
        Console.WriteLine("抵免验证通过：国家隔离、原币分配、账户、超额、凭证组合、重复凭证、所得关系、排除、冻结另存与隐私、底稿、取消（12 项）。");
    }
}
