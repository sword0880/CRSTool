using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;

internal static class ExportChecks
{
    /// <summary>验证真实文件失败与取消不替换原件、临时清理及锁定不等待写入。</summary>
    internal static async Task RunAsync(string root)
    {
        var directory=Path.Combine(root,"export-checks"); Directory.CreateDirectory(directory);
        var target=Path.Combine(directory,"existing.xlsx"); const string original="SYNTHETIC-EXISTING-REPORT";
        File.WriteAllText(target,original);
        var result=new CalculationResult {Year=2025,SnapshotId="EXPORT-CHECK",Summary=new(100,0,0,20,0,0,20)};
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(()=>ExcelReports.Export(target,result,cancellation:cancelled.Token));
        Unchanged();
        // 在 Excel 行构建中取消，不能静默完成剩余行或写入目标文件。
        using var rowsCancellation=new CancellationTokenSource();
        IEnumerable<ReviewNote> Rows()
        {
            yield return new(result.SnapshotId,"合成复核","synthetic","synthetic");
            rowsCancellation.Cancel();
            yield return new(result.SnapshotId,"合成复核","must-not-export","synthetic");
        }
        Reject<OperationCanceledException>(()=>ExcelReports.Export(target,result,Rows(),rowsCancellation.Token)); Unchanged();
        Reject<IOException>(()=>AtomicExport.Write(target,stream=> {stream.Write([1,2,3]); throw new IOException("模拟磁盘写入失败。");},default)); Unchanged();
        // 模拟目标被其他应用占用：提交失败也必须清理暂存文件，不破坏旧文件。
        using(var occupied=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            var rejected=false;
            try {AtomicExport.Write(target,stream=>stream.Write([1,2,3]),default);}
            catch(Exception error) when(error is IOException or UnauthorizedAccessException) {rejected=true;}
            if(!rejected) throw new Exception("被占用的目标文件仍被替换。");
        }
        Unchanged();
        using var writingCancellation=new CancellationTokenSource();
        Reject<OperationCanceledException>(()=>AtomicExport.Write(target,stream=>
        {stream.Write([1,2,3]); writingCancellation.Cancel(); stream.Write([4]);},writingCancellation.Token)); Unchanged();
        using var finalCancellation=new CancellationTokenSource();
        Reject<OperationCanceledException>(()=>AtomicExport.Write(target,stream=>
        {stream.Write([1,2,3]); finalCancellation.Cancel();},finalCancellation.Token)); Unchanged();
        var fresh=Path.Combine(directory,"new.xlsx");
        Reject<IOException>(()=>AtomicExport.Write(fresh,stream=> {stream.Write([1]); throw new IOException();},default));
        if(File.Exists(fresh)) throw new Exception("失败的新导出留下半成品目标文件。"); Clean();
        // 实际 SQLCipher 会话：写入暂停期间锁定必须及时完成，旧会话不能提交。
        using var vault=new VaultManager(); await vault.CreateAsync(Path.Combine(root,"export-vault"),"Synthetic-Export-Password");
        await TestStores.Enroll(vault); var guard=vault.CaptureGuard();
        var writing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resume=new ManualResetEventSlim();
        var export=Task.Run(()=>AtomicExport.Write(target,stream=>
        {stream.Write([1,2,3]); writing.SetResult(); if(!resume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("写入暂停未释放。");},default,guard));
        try
        {
            await writing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Run(vault.Lock).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally {resume.Set();}
        try {await export; throw new Exception("锁定后旧导出仍提交。");} catch(CrsException) { }
        Unchanged();
        ExcelReports.Export(target,result);
        if(CRS.Documents.Excel.ExcelReader.ReadSheet(target,"税务汇总")[1][1]!="100.00") throw new Exception("成功导出内容无效。"); Clean();
        // 可信结转也经同一提交路径，取消时不破坏已存在的结转文件。
        var carryResult=new CalculationResult {Year=2025,SnapshotId="CARRY-CHECK",Summary=new(0,0,0,0,0,0,0),CarryEligible=true,
            Sources=[new("synthetic.xml","synthetic","ACCOUNT",new(2025,1,1),new(2025,12,31),true,true,true,Opening:false)]};
        var carry=Path.Combine(directory,"carry.json"); var operations=new DesktopOperations(null!,root);
        operations.SaveCarry(carry,carryResult); var before=File.ReadAllBytes(carry);
        Reject<OperationCanceledException>(()=>operations.SaveCarry(carry,carryResult,cancelled.Token));
        if(!before.SequenceEqual(File.ReadAllBytes(carry)) || CarryFiles.Read(carry).SourceSnapshotId!="CARRY-CHECK") throw new Exception("取消结转改变原文件或成功结转无法读取。"); Clean();
        Console.WriteLine("导出验证通过：预取消、行构建取消、写入失败／取消、提交前取消、新文件清理、真实锁定不中断原文件、Excel 与结转有效输出。");
        // 未知金额和已确认零值在真实工作簿中必须具有不同单元格语义。
        var partialPath=Path.Combine(directory,"partial-amount.xlsx");
        ExcelReports.Export(partialPath,new CalculationResult {Year=2025,Summary=new(10,0,0,2,0,0,2),EstimatedTopUpCny=2,UnknownGainCount=1});
        var amountRows=CRS.Documents.Excel.ExcelReader.ReadSheet(partialPath,"税务汇总");
        if(amountRows[8][1]!="" || !amountRows[8][2].Contains("不可确定") || amountRows[7][1]!="2.00" || !amountRows[3][2].Contains("1 笔"))
            throw new Exception("工作簿未知金额未留空或原始公式语义丢失。");
        var zeroPath=Path.Combine(directory,"zero-amount.xlsx");
        ExcelReports.Export(zeroPath,new CalculationResult {Year=2025,Summary=new(0,0,0,0,0,0,0),EstimatedTopUpCny=0,Reconciled=true,
            CalculationStatus=CalculationStatus.Completed,DataCompleteness=DataCompleteness.Confirmed,
            ReconciliationStatus=ReconciliationStatus.Matched,UsageLabel=UsageLabel.ReviewReady});
        if(CRS.Documents.Excel.ExcelReader.ReadSheet(zeroPath,"税务汇总")[8][1]!="0.00") throw new Exception("工作簿把真实零值当作未知。");
        Console.WriteLine("金额底稿通过：未知预计值留空、说明保留、原始公式分列及真实零值输出。");
        void Clean() {if(Directory.EnumerateFiles(directory,"*.tmp").Any()) throw new Exception("导出失败或取消残留临时文件。");}
        void Unchanged() {if(File.ReadAllText(target)!=original) throw new Exception("失败或取消覆盖已有报告。"); Clean();}
    }
    private static void Reject<T>(Action action) where T:Exception
    {
        try {action();} catch(T) {return;}
        throw new Exception("预期失败或取消的导出未被阻断。");
    }
}
