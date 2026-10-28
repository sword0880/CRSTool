using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CRS.IO.Staging;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

internal static class MarketBreadthPipeChecks
{
    public static async Task Run(string package,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);if(Directory.Exists(root))throw new IOException("验收目录必须不存在。");Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team/2.0.0");Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(Path.Combine(package,"Plugins/crs.national-team/2.0.0"),"*",SearchOption.AllDirectories)){var destination=Path.Combine(target,Path.GetRelativePath(Path.Combine(package,"Plugins/crs.national-team/2.0.0"),file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(root,"service");Directory.CreateDirectory(Path.Combine(data,"plugins/crs.national-team/market-knowledge.fixture"));
        var pipe="CRS.Breadth."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var token=deadline.Token;var passed=0;
        void Check(bool value,string text){if(!value)throw new InvalidDataException(text);passed++;Console.WriteLine("PASS 广度真实管道："+text);}
        async Task<BackgroundResponse> Send(BackgroundRequest request){while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not ("RateLimited" or "PluginBusy" or "Busy"))return reply;await Task.Delay(250,token);}}
        async Task<object> Receipt(Guid id)
        {
            var reply=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id));Check(reply.Receipt is{State:"Completed",Code:"Succeeded",ExecutionId:not null},"实际命令持久回执已成功完成");
            var execution=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:reply.Receipt!.ExecutionId));Check(execution.Execution?.Result=="Succeeded","对应执行终态已独立回读");return new{reply.Receipt,execution.Execution};
        }
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var argument in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(argument);
        using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            BackgroundResponse health;while(true){token.ThrowIfCancellationRequested();if(process.HasExited)throw new IOException("候选宿主提前退出。");try{health=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(health.Health?.State=="Running")break;}catch(IOException){}await Task.Delay(250,token);}
            var workspace=new WorkerEtfWorkspace(client);Check(workspace.CanManage,"隔离进程使用实际管理员管道权限");
            var day=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime).AddDays(-7);var acquired=DateTimeOffset.UtcNow.AddMinutes(-1);
            var members=Enumerable.Range(0,300).Select(x=>"SSE:"+(600000+x)).ToArray();var observations=members.Select(x=>new MarketBreadthMember(x,10,9,9,8)).ToArray();var archive=new MarketBreadthArchive("reviewed-breadth-v1",day,"SSE-SZSE-Ashares-point-in-time-v1","synthetic-only-universe",day,"BackAdjustedToObservationDate",members,observations);
            var originalPath=Path.Combine(root,"synthetic-original.bin");var normalizedPath=Path.Combine(root,"synthetic-normalized.json");var original=Enumerable.Range(0,70001).Select(x=>(byte)(x%251)).ToArray();File.WriteAllBytes(originalPath,original);File.WriteAllBytes(normalizedPath,JsonSerializer.SerializeToUtf8Bytes(archive));
            var submission=await workspace.SubmitMarketBreadthAsync(originalPath,normalizedPath,"https://example.invalid/synthetic-breadth",acquired,"synthetic-pipe-submitter","仅用于隔离管道验收",token);
            var pending=await workspace.MarketBreadthHistoryAsync(day,token);Check(pending.Rows.Single() is{Decision:"NeedsReview",UniverseCount:300},"超过4KB的真实文件经有界暂存、Runner和后台解析后保持待核验");
            var submittedReceipt=await Receipt(submission.RequestId);Check((await workspace.MarketCycleStudyAsync(day,null,token)).Input.BreadthPoints.Single().State==MarketCycleObservationState.NeedsReview,"实际周期查询拒绝把登记当作核验通过");
            var inbox=Path.Combine(data,"plugins/crs.national-team/document-inbox");Check(Directory.GetDirectories(inbox).Length==2&&!Directory.EnumerateFiles(inbox,"*.block",SearchOption.AllDirectories).Any(),"成功登记后保留暂存消费身份，移除暂存字节副本");
            var exported=Path.Combine(root,"exported-original.bin");await workspace.ExportMarketBreadthEvidenceAsync(submission.RevisionId,true,exported,token);Check(File.ReadAllBytes(exported).AsSpan().SequenceEqual(original),"待核验原件可通过实际分块接口完整导出核对");
            var invalid=false;try{await workspace.ReviewMarketBreadthAsync(new(submission.RevisionId,day,pending.StateFingerprint,true,"synthetic-pipe-reviewer","未确认原件",false),token);}catch(InvalidDataException){invalid=true;}Check(invalid&&(await workspace.MarketBreadthHistoryAsync(day,token)).Rows.Single().Decision=="NeedsReview","缺少原件确认时持久命令被拒绝，未改变状态");
            await workspace.ReviewMarketBreadthAsync(new(submission.RevisionId,day,pending.StateFingerprint,true,"synthetic-pipe-reviewer","已核对合成原件",true),token);var accepted=await workspace.MarketBreadthHistoryAsync(day,token);Check(accepted.Rows.Single().Decision=="Accepted","独立管理员审核后才产生可研究广度");var approvalReceipt=await Receipt(Guid.ParseExact(accepted.Rows[0].StateCommandId!,"N"));
            archive=archive with{Observations=observations.Select((x,i)=>i==0?x with{Close=11}:x).ToArray()};File.WriteAllBytes(normalizedPath,JsonSerializer.SerializeToUtf8Bytes(archive));
            var second=await workspace.SubmitMarketBreadthAsync(originalPath,normalizedPath,"https://example.invalid/synthetic-breadth",acquired,"synthetic-pipe-submitter","另一合成修订",token);var conflict=await workspace.MarketBreadthHistoryAsync(day,token);
            Check(conflict.Rows.Count==2&&(await workspace.MarketCycleStudyAsync(day,null,token)).Input.BreadthPoints.Count==2,"新原件修订跨进程保留竞争，完整信号继续关闭");
            var stale=false;try{await workspace.ReviewMarketBreadthAsync(new(second.RevisionId,day,accepted.StateFingerprint,true,"synthetic-pipe-reviewer","过期窗口",true),token);}catch(InvalidDataException){stale=true;}Check(stale,"旧窗口摘要不能批准新修订");
            await workspace.ReviewMarketBreadthAsync(new(second.RevisionId,day,conflict.StateFingerprint,true,"synthetic-pipe-reviewer","选定新合成版",true),token);var selected=await workspace.MarketBreadthHistoryAsync(day,token);
            Check(selected.Rows.Single(x=>x.Id==submission.RevisionId).Decision=="Superseded"&&selected.Rows.Single(x=>x.Id==second.RevisionId).Decision=="Accepted"&&(await workspace.MarketCycleStudyAsync(day,null,token)).Input.BreadthPoints.Single().RevisionId==second.RevisionId,"选定与替代完成后周期只读取明确选定的新版本");
            var selectedReceipt=await Receipt(Guid.ParseExact(selected.Rows.Single(x=>x.Id==second.RevisionId).StateCommandId!,"N"));
            var finalHealth=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(finalHealth.Health?.State=="Running"&&finalHealth.Health.ProcessId==health.Health!.ProcessId,"大文件与审核没有停止后台宿主，PID保持一致");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticMarketBreadthWorkflow",submission,second,Pending=pending,Accepted=accepted,Conflict=conflict,Selected=selected,submittedReceipt,approvalReceipt,selectedReceipt,Health=finalHealth.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();File.WriteAllText(Path.Combine(root,"stdout.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr.log"),await stderr);}
    }
}
