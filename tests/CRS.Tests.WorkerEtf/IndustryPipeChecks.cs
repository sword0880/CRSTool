using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;

/// <summary>完整宿主/监督进程/真实管道使用公开原件和显式合成日历；不接触正式服务或网络。</summary>
internal static class IndustryPipeChecks
{
    public static async Task Run(string package,string sourceFile,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);if(Directory.Exists(root))throw new IOException("管道验收目录已存在，拒绝覆盖。");Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var source=Path.Combine(package,"Plugins","crs.national-team","2.0.0");var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team","2.0.0");Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;
        manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(root,"service");var fixture=Path.Combine(data,"plugins","crs.national-team","industry-pe.fixture");Directory.CreateDirectory(fixture);File.Copy(sourceFile,Path.Combine(fixture,"2026-10-23.json"));
        var ranges=new[]{(1,1,1,3),(2,15,2,23),(4,4,4,6),(5,1,5,5),(6,19,6,21),(9,25,9,27),(10,1,10,7)};
        File.WriteAllText(Path.Combine(fixture,"calendar-2026.html"),"合成夹具：2026年部分节假日休市安排，周末休市。"+string.Join('。',ranges.Select(x=>$"{x.Item1}月{x.Item2}日（星期一）至{x.Item3}月{x.Item4}日（星期二）休市")));
        var pipeName="CRS.IndustryPe."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipeName);using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=deadline.Token;var passed=0;
        void Check(bool ok,string label){if(!ok)throw new InvalidDataException(label);passed++;Console.WriteLine("PASS 行业真实管道："+label);}
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var result=await client.SendAsync(request,token);if(result.Code is not ("RateLimited" or "PluginBusy"))return result;await Task.Delay(250,token);}}
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};foreach(var value in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipeName})info.ArgumentList.Add(value);
        using var process=Process.Start(info)!;process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
        try
        {
            BackgroundResponse health;
            while(true)
            {
                token.ThrowIfCancellationRequested();if(process.HasExited)throw new InvalidDataException("隔离宿主已经退出。");
                try{health=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(health.Health?.State=="Running")break;}catch(IOException){ }
                await Task.Delay(250,token);
            }
            Check(health.Health?.State=="Running","完整包启动且背景入口保持无WPF加载依赖");
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","industry-pe-sync",Payload:JsonSerializer.Serialize(new EtfRequest("industry-pe-sync",From:new(2026,10,23),To:new(2026,10,23))));
            var accepted=await Send(request);Check(accepted.Code=="Accepted","手动行业命令进入持久队列");
            BackgroundResponse status;
            do{status=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));if(status.Receipt?.State!="Completed")await Task.Delay(250,token);}while(status.Receipt?.State!="Completed");
            Check(status.Receipt?.Code=="Succeeded","监督执行完成并按原请求核对");
            var reader=new WorkerEtfWorkspace(client);var report=await reader.IndustryPeReportAsync(new(2026,10,23),new(2026,10,23),token);var revisions=await reader.IndustryPeRevisionsAsync(new(2026,10,23),new(2026,10,23),token);
            Check(report.Rows.Count==11&&report.Rows.Single(x=>x.Code=="C07").LatestRollingWeighted==7.36m,"11行业跨快照分页回读且黄金值正确");
            Check(revisions.Count==11&&revisions.All(x=>x.State=="Accepted"&&x.EvidenceHealthy),"独立金融修订及原件健康一致");
            Check(report.Rows.All(x=>x.PublishedSeriesPercentile is null)&&report.OpportunityState=="NotEvaluatedMissingCoveragePbFundamentals","单日取得不能冒充历史低估机会");
            _=await Send(request);var repeated=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));Check(repeated.Receipt?.ExecutionId==status.Receipt?.ExecutionId,"同请求保持执行身份，不重复联网");
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health?.ProcessId==health.Health?.ProcessId&&after.Health?.State=="Running","读取前后同一健康宿主");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Transport="CapturedOfficialIndustryFileFixture",Calendar="SyntheticReviewedFormatFixture",Request=request,Receipt=status.Receipt,Report=report,Revisions=revisions,Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();}
    }
}
