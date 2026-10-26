using CRS.Plugin.Common.Concurrency;

internal static class PluginCommonAdmissionChecks
{
    static void Check(bool ok,string label)
    {if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);}
    public static async Task RunAsync()
    {
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
        Check(background.IsCompleted,"one background request proceeds after eight priority grants");
        using(await background.WaitAsync(TimeSpan.FromSeconds(3))) { }
        using(await queries[8].WaitAsync(TimeSpan.FromSeconds(3))) { }
        Check(gate.Activity.Active==0 && gate.Activity.PriorityWaiting==0 && gate.Activity.BackgroundWaiting==0,
            "all capacity returned on disposal");
        var another=new PriorityReadAdmission(1);
        using(await another.EnterAsync(false,CancellationToken.None))
            Check(another.Activity.Active==1 && gate.Activity.Active==0,"two plugins maintain isolated quotas");
        Check(another.Activity.Active==0,"isolated lease disposes");
    }
}
