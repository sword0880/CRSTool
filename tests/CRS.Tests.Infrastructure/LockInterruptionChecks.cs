using CRS.Application;
using CRS.Domain;
using CRS.Infrastructure;
using Dapper;

internal static class LockInterruptionChecks
{
    /// <summary>在实际原生备份步之后暂停，系统锁定必须中断，而非等待整个备份完成。</summary>
    internal static async Task RunAsync(string root)
    {
        const string password="Synthetic-Lock-Interruption-2026";
        var directory=Path.Combine(root,"interruptible-vault"); using var vault=new VaultManager();
        await vault.CreateAsync(directory,password); await TestStores.Enroll(vault);
        var old=vault.Repository;
        old.Save(new CalculationResult {Year=2025,SnapshotId="KEEP",Summary=new(1,0,0,0,0,0,0)});
        using(var c=vault.OpenForTest()) c.Execute("CREATE TABLE synthetic_padding(data BLOB); INSERT INTO synthetic_padding VALUES(zeroblob(4194304));");
        using var reached=new ManualResetEventSlim();
        var target=Path.Combine(root,"must-not-commit.crsbak");
        var backup=vault.BackupCoreAsync(target,afterStep:token=>
        {
            reached.Set(); if(!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new Exception("锁定没有及时发出中断。");
            token.ThrowIfCancellationRequested();
        });
        if(!await Task.Run(()=>reached.Wait(TimeSpan.FromSeconds(10)))) throw new Exception("原生备份步未开始。");
        var queuedPath=Path.Combine(root,"queued-must-not-revive.crsbak"); var queued=vault.BackupAsync(queuedPath);
        await Task.Run(vault.Lock).WaitAsync(TimeSpan.FromSeconds(5));
        await Cancelled(backup); await Cancelled(queued);
        if(File.Exists(target) || File.Exists(queuedPath) || Directory.GetFiles(directory,".backup-*").Length!=0
            || Directory.GetFiles(root,"*.tmp").Length!=0) throw new Exception("锁定后提交了备份或遗留临时快照。");
        try {old.History(); throw new Exception("锁定前仓储仍可访问。");} catch(CrsException) { }
        await vault.UnlockAsync(directory,password); await TestStores.Authenticate(vault);
        if(vault.Repository.Load("KEEP").Summary.DividendCny!=1) throw new Exception("取消备份损坏原始历史。");
        var valid=Path.Combine(root,"after-unlock.crsbak"); await vault.BackupAsync(valid); vault.Lock();
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
        var restoreTarget=Path.Combine(root,"cancelled-restore");
        await Cancelled(vault.RestoreAsync(valid,restoreTarget,password,false,password,cancelled.Token));
        if(Directory.Exists(restoreTarget)) throw new Exception("已取消恢复留下目标目录。");
        Console.WriteLine("锁定中断通过：原生分步备份取消、排队任务不复活、旧仓储失效、原历史保留、重新解锁可备份及恢复取消。");
    }
    private static async Task Cancelled(Task task)
    {try {await task;} catch(OperationCanceledException) {return;} throw new Exception("旧操作未按取消结束。");}
}
