using CRS.Worker.Ipc;

internal static class BackgroundCommandTrackerChecks
{
    public static void Run()
    {
        var passed=0;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);passed++;Console.WriteLine("PASS 共用命令跟踪："+text);}
        void Reject(Action action,string reason)
        {try{action();throw new Exception("未拒绝命令跟踪冲突。");}catch(BackgroundCommandTrackingException error){Check(error.Reason==reason,"明确拒绝："+reason);}}
        var tracker=new BackgroundCommandTracker(2);
        var first=tracker.Acquire("plugin-a","job-a","opaque-first");var request=first.Request;
        Check(first.FirstAttempt&&request.Command=="RunNow"&&request.RequestId!=Guid.Empty,"首次请求分配真实ID");
        Reject(()=>tracker.Acquire("plugin-a","job-a","opaque-first"),"InFlight");
        using(var other=tracker.Acquire("plugin-a","job-b","opaque-other"))
            Check(other.Request.RequestId!=request.RequestId,"同插件不同任务可以同时跟踪");
        Reject(()=>tracker.Acquire("plugin-b","job-a","opaque-third"),"Capacity");
        first.Dispose();first.Dispose();
        using(var retry=tracker.Acquire("plugin-a","job-a","opaque-first"))
            Check(retry.Request.RequestId==request.RequestId&&!retry.FirstAttempt,"未知结果释放等待租约后仍沿用原ID");
        Reject(()=>tracker.Acquire("plugin-a","job-a","changed-parameters"),"Conflict");
        Check(tracker.Find("plugin-a","job-a")==request,"冲突和容量拒绝都不会丢失旧请求");
        tracker.Forget(request);
        using(var next=tracker.Acquire("plugin-a","job-a","changed-parameters"))
        {
            Check(next.FirstAttempt&&next.Request.RequestId!=request.RequestId,"已确认释放后才能为新意图分配新ID");
            tracker.Forget(request);
            Check(tracker.Find("plugin-a","job-a")==next.Request,"迟到的旧终态不能清空新请求");
            tracker.Forget(next.Request);
        }
        var separate=new BackgroundCommandTracker();
        using(var a=separate.Acquire("plugin-a","same-job",null))
        using(var b=separate.Acquire("plugin-b","same-job",null))
            Check(a.Request.RequestId!=b.Request.RequestId,"同任务名跨插件隔离");
        Console.WriteLine($"COMMAND_TRACKER passed={passed};network=none;filesystem=none");
    }
}
