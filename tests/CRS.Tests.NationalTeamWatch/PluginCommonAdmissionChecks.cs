using CRS.Common.Concurrency;
using CRS.Common.Integrity;
using System.Text;

internal static class PluginCommonAdmissionChecks
{
    static void Check(bool ok,string label)
    {if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);}
    public static async Task RunAsync()
    {
        var abc=Encoding.UTF8.GetBytes("abc");
        const string expected="BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
        Check(ContentFingerprint.Sha256(abc)==expected,"shared SHA256 identity is deterministic and canonical");
        Check(ContentFingerprint.IsSha256Hex(expected)&&ContentFingerprint.IsSha256Hex(expected.ToLowerInvariant()),"legacy hex validation accepts both cases");
        Check(ContentFingerprint.Matches(expected,abc)&&!ContentFingerprint.Matches(expected,Encoding.UTF8.GetBytes("abd")),
            "content identity verifies exact bytes and detects corruption");
        Check(!ContentFingerprint.Matches(expected.ToLowerInvariant(),abc)&&!ContentFingerprint.IsSha256Hex("../evidence"),
            "canonical comparison remains case-sensitive and invalid names are rejected");
        foreach(var invalid in new[]{0,-1,257})
        {
            try { _=new PriorityReadAdmission(invalid);throw new Exception("invalid capacity accepted"); }
            catch(ArgumentOutOfRangeException) { }
        }
        var gate=new PriorityReadAdmission(1);
        var held=await gate.EnterAsync(true,CancellationToken.None);
        using var cancel=new CancellationTokenSource();
        var pending=gate.EnterAsync(true,cancel.Token).AsTask();
        Check(gate.Activity.PriorityWaiting==1,"priority queue visible");
        cancel.Cancel();
        try { await pending; throw new Exception("cancel ignored"); }
        catch(OperationCanceledException) { }
        Check(gate.Activity.PriorityWaiting==0,"cancellation clears waiting count");
        var background=gate.EnterAsync(false,CancellationToken.None).AsTask();
        var queries=Enumerable.Range(0,9).Select(_=>gate.EnterAsync(true,CancellationToken.None).AsTask()).ToArray();
        Check(gate.Activity.Active==1 && gate.Activity.PriorityWaiting==9 && gate.Activity.BackgroundWaiting==1,
            "bounded queue separates priority and background");
        held.Dispose();
        for(var i=0;i<8;i++)
            using(await queries[i].WaitAsync(TimeSpan.FromSeconds(3))) { }
        // TCS continuations run asynchronously: IsCompleted may still be false immediately after lease grant.
        // Hold the ninth priority lease queued; the background lease must be granted first or this times out.
        using(await background.WaitAsync(TimeSpan.FromSeconds(3)))
            Check(gate.Activity.Active==1,"one background request proceeds after eight priority grants");
        using(await queries[8].WaitAsync(TimeSpan.FromSeconds(3))) { }
        Check(gate.Activity.Active==0 && gate.Activity.PriorityWaiting==0 && gate.Activity.BackgroundWaiting==0,
            "all capacity returned on disposal");
        var another=new PriorityReadAdmission(1);
        using(await another.EnterAsync(false,CancellationToken.None))
            Check(another.Activity.Active==1 && gate.Activity.Active==0,"two plugins maintain isolated quotas");
        Check(another.Activity.Active==0,"isolated lease disposes");
        var raced=new PriorityReadAdmission(1);
        var reservation=await raced.EnterAsync(false,CancellationToken.None);
        using var raceCancel=new CancellationTokenSource();
        var contender=raced.EnterAsync(true,raceCancel.Token).AsTask();
        reservation.Dispose();
        raceCancel.Cancel();
        try { using(await contender.WaitAsync(TimeSpan.FromSeconds(3))) { } }
        catch(OperationCanceledException) { }
        Check(raced.Activity.Active==0 && raced.Activity.PriorityWaiting==0,
            "cancel/grant race never leaks shared read capacity");
        await BoundedWorkloads();
    }
    private static async Task BoundedWorkloads()
    {
        var gate=new BoundedWorkAdmission(1,1,TimeSpan.FromMilliseconds(60));
        using(var held=await gate.EnterAsync(default))
        {
            var waiting=gate.EnterAsync(default).AsTask();
            try{using var extra=await gate.EnterAsync(default);throw new Exception("无界排队。");}
            catch(WorkAdmissionException error) when(error.Reason=="Capacity"){Check(true,"工作队列超过有限等待名额即拒绝");}
            try{using var expired=await waiting;throw new Exception("无限等待。");}
            catch(WorkAdmissionException error) when(error.Reason=="WaitBudget"){Check(true,"等待预算到期明确失败，不进入执行");}
            using var cancellation=new CancellationTokenSource();var cancelled=gate.EnterAsync(cancellation.Token).AsTask();cancellation.Cancel();
            try{using var ignored=await cancelled;throw new Exception("忽略取消。");}catch(OperationCanceledException){}
            var next=gate.EnterAsync(default).AsTask();held.Dispose();using(await next){Check(true,"取消移除排队项，下一请求可取得释放的名额");}
        }
        var draining=new BoundedWorkAdmission(2,2,TimeSpan.FromSeconds(1));
        var first=await draining.EnterAsync(default);var second=await draining.EnterAsync(default);var pending=draining.EnterAsync(default).AsTask();
        var close=draining.CloseAsync();
        try{using var ignored=await pending;throw new Exception("关闭仍等待。");}catch(WorkAdmissionException error) when(error.Reason=="Closed"){}
        first.Dispose();Check(!close.IsCompleted,"关闭仍等待第二个在途读租约，不能只排空一个名额");second.Dispose();await close;
        try{using var ignored=await draining.EnterAsync(default);throw new Exception("关闭后重新准入。");}
        catch(WorkAdmissionException error) when(error.Reason=="Closed"){Check(true,"关闭完成后拒绝新工作");}
        for(var i=0;i<60;i++)
        {
            var slot=await gate.EnterAsync(default);using var stop=new CancellationTokenSource();var racing=gate.EnterAsync(stop.Token).AsTask();
            slot.Dispose();stop.Cancel();try{using var raced=await racing;}catch(OperationCanceledException){}
        }
        using(await gate.EnterAsync(default)){Check(true,"取消与授予竞争重复60次后不泄漏容量");}
    }
}
