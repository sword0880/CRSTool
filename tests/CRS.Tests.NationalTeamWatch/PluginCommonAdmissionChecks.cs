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
    }
}
