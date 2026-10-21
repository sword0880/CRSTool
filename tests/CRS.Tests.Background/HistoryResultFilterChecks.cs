using System.Security.Cryptography;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;

/// <summary>独立加密仓储和只读控制层：确保异常筛选发生在服务端分页前，且可识别旧协议忽略。</summary>
internal static class HistoryResultFilterChecks
{
    private static void Check(bool ok,string name)
    {
        if(!ok)throw new InvalidOperationException("最近执行结果筛选："+name);
        Console.WriteLine("PASS 最近执行结果筛选："+name);
    }

    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var key=RandomNumberGenerator.GetBytes(32);
        using var storage=new EncryptedTaskRepository(Path.Combine(root,"history.db"),key);
        try
        {
            var descriptor=new BackgroundJobDescriptor("market-activity-sync","成交统计采集","0 0 23 * * ?","China Standard Time",true);
            storage.RegisterDefault("crs.national-team",descriptor);
            var plan=storage.Plans().Single();
            // 多于一个页面，混合普通完成、暂未发布、主动取消、仍运行、重试延期、中断及异常。
            var statuses=new[] {"Succeeded","RetryDeferred","NotPublished","Failed","Cancelled","AccessBlocked","SourceSchemaChanged","Interrupted","Rejected","Succeeded","RetryDeferred","Failed","NotPublished","Cancelled","Failed"};
            foreach(var result in statuses)
            {
                var id=storage.Begin(plan,DateTimeOffset.UtcNow);
                storage.End(id,plan,new(result));
            }
            var all=storage.HistoryPage(null,10,plugin:"crs.national-team");
            var errors=new List<ExecutionRecord>();
            string? cursor=null;var visited=new HashSet<string>();
            do
            {
                var page=storage.HistoryPage(cursor,3,plugin:"crs.national-team",resultFilter:"Issues");
                Check(page.Rows.All(x=>x.Result is not ("Succeeded" or "NotPublished" or "Running" or "Cancelled")),
                    "每次读取的异常页不混入普通结果");
                foreach(var row in page.Rows)Check(visited.Add(row.ExecutionId),"多页结果没有重复");
                errors.AddRange(page.Rows);
                cursor=page.NextCursor;
            }while(cursor is not null);
            Check(errors.Count==statuses.Count(x=>x is not ("Succeeded" or "NotPublished" or "Running" or "Cancelled")),
                "异常筛选在数据库分页之前执行，跨页没有遗漏");
            Check(errors.Select(x=>x.Result).Contains("Interrupted") && errors.Select(x=>x.Result).Contains("RetryDeferred"),
                "包括强杀中断与退避延期，不误判尚未发布为故障");
            Check(all.Rows.Any(x=>x.Result=="Succeeded"),"普通查询仍能看到成功与失败混合记录");
            Check(storage.HistoryPage(null,10,plugin:"crs.heartbeat",resultFilter:"Issues").Rows.Count==0,
                "插件条件和异常条件可同时应用");
            try { storage.HistoryPage(null,10,resultFilter:"anything-goes");throw new InvalidOperationException("未知筛选被错误接受"); }
            catch(ArgumentException){Check(true,"未知结果筛选显式拒绝");}

            await using var catalog=new BackgroundPluginCatalog();
            await using var runtime=new BackgroundRuntime(storage,catalog);
            var caller=new BackgroundCaller("test-readonly",false);
            var response=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",
                PluginId:"crs.national-team",PageSize:4,ResultFilter:"Issues"),caller,default);
            Check(response.Code=="Ok" && response.AppliedResultFilter=="Issues" && response.History is {Count:4} &&
                  response.History.All(x=>x.Result is not ("Succeeded" or "NotPublished" or "Running" or "Cancelled")),
                "只读IPC显式确认过滤且首屏即为异常4条");
            var next=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",
                PluginId:"crs.national-team",Cursor:response.NextCursor,PageSize:4,ResultFilter:"Issues"),caller,default);
            Check(next.Code=="Ok" && next.AppliedResultFilter=="Issues" && next.History?.Count>0 &&
                  next.History.All(x=>x.Result is not ("Succeeded" or "NotPublished" or "Running" or "Cancelled")),
                "只读IPC翻页仍确认结果筛选");
            var bad=await runtime.HandleAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetFilteredHistory",
                ResultFilter:"arbitrary"),caller,default);
            Check(bad.Code=="InvalidRequest","非法结果筛选参数经协议明确拒绝");
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
}
