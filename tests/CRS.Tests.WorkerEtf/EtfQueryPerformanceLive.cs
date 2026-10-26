using System.Diagnostics;
using System.Security.Cryptography;
using System.IO;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;

/// <summary>正式业务只读测量；失败同样核对健康和PID，不提交任务、不清缓存、不提取服务密钥。</summary>
internal static class EtfQueryPerformanceLive
{
    public static async Task RunAsync(string output,bool awaitNaturalLoad=false)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("测量目录已存在，保留原测量不覆盖。");Directory.CreateDirectory(output);
        var pipe=new BackgroundPipeClient();var measurements=new List<EtfQueryPageMeasurement>();var reader=new WorkerEtfWorkspace(pipe,measurements.Add);var results=new List<object>();var failed=0;
        async Task<BackgroundResponse> Health()
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));return await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"),timeout.Token);
        }
        if(awaitNaturalLoad)
        {
            // 等正常在途任务，不排队新采集来制造负载；六分钟只限制观察，产品单次查询仍为15秒。
            var waitStarted=CRS.Timing.BeijingClock.Now;var elapsed=Stopwatch.StartNew();BackgroundResponse? observed=null;
            while(elapsed.Elapsed<TimeSpan.FromMinutes(6))
            {
                observed=await Health();
                if(observed.Health?.State=="Running"&&observed.Health.Scheduling?.Plugins?.Any(x=>x.PluginId=="crs.national-team"&&x.Running>=2)==true)break;
                await Task.Delay(2000);
            }
            var loadObserved=observed?.Health?.State=="Running"&&observed.Health.Scheduling?.Plugins?.Any(x=>x.PluginId=="crs.national-team"&&x.Running>=2)==true;
            var activity=loadObserved?await pipe.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity")):null;
            await File.WriteAllTextAsync(Path.Combine(output,"natural-load.json"),JsonSerializer.Serialize(new{StartedAt=waitStarted,ObservedAt=CRS.Timing.BeijingClock.Now,Observed=loadObserved,Health=observed?.Health,Activity=activity},new JsonSerializerOptions{WriteIndented=true}));
            if(!loadObserved){Console.WriteLine("Unavailable: 未观察到两项国家队正常在途任务，未追加工作或假造忙时验收。");Environment.ExitCode=2;return;}
        }
        async Task Sample<T>(string name,Func<CancellationToken,Task<T>> read,Func<T,object> summarize)
        {
            measurements.Clear();var before=await Health();var timer=Stopwatch.StartNew();object? summary=null;string? contentHash=null;Exception? error=null;
            // 45秒仅是完整分页测量上限，产品/宿主的单次查询15秒限额未调整。
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try{var data=await read(timeout.Token);summary=summarize(data);contentHash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));}catch(Exception caught){error=caught;failed++;}
            var elapsed=timer.ElapsedMilliseconds;BackgroundResponse? after=null;string? healthError=null;
            try{after=await Health();}catch(Exception caught){healthError=caught.GetType().Name;failed++;}
            var healthy=before.Health?.State=="Running"&&after?.Health?.State=="Running"&&before.Health.ProcessId==after.Health.ProcessId;
            if(!healthy)failed++;
            var result=new{Name=name,CheckedAt=CRS.Timing.BeijingClock.Now,Status=error is null&&healthy?"Succeeded":"Unavailable",ElapsedMs=elapsed,
                Before=before.Health,After=after?.Health,SameHealthyPid=healthy,HealthError=healthError,Error=error?.GetType().Name,Message=error?.Message,
                Data=error is null&&healthy?summary:null,ResultContentSha256=error is null&&healthy?contentHash:null,Pages=measurements.ToArray(),PageCount=measurements.Count,
                ItemsBytes=measurements.Sum(x=>x.ItemsBytes),FirstPageMs=measurements.FirstOrDefault()?.ElapsedMs,
                SubsequentPageMs=measurements.Skip(1).Sum(x=>x.ElapsedMs),FilesystemColdState="NotControlled",ApplicationVerificationCache="NotUsed"};
            results.Add(result);await File.WriteAllTextAsync(Path.Combine(output,name+".json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(new{Name=name,result.Status,result.ElapsedMs,result.PageCount,result.FirstPageMs,result.SubsequentPageMs,result.SameHealthyPid,result.Error}));
        }
        for(var i=0;i<3;i++)await Sample("dashboard-"+i,t=>reader.DashboardAsync(null,t),value=>value is null?throw new InvalidDataException("正式后台不支持完整整页查询。"):new{Overview=value.Overview.Count,History=value.History.Count,Activity=value.Activity.Count});
        await Sample("history-510300",t=>reader.HistoryAsync(new EtfKey("SSE","510300"),t),rows=>new{Rows=rows.Count});
        await Sample("evidence-510300",t=>reader.EvidenceAsync(new EtfKey("SSE","510300"),t),rows=>new{Rows=rows.Count});
        await File.WriteAllTextAsync(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new{ReadOnly=true,Failed=failed,Results=results},new JsonSerializerOptions{WriteIndented=true}));
        if(failed>0)Environment.ExitCode=2;
    }
}
