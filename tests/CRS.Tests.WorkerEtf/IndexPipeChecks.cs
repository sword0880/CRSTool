using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;

/// <summary>完整宿主/监督进程/真实管道使用公开原件和显式合成日历；不接触正式服务或网络。</summary>
internal static class IndexPipeChecks
{
    public static async Task Run(string package,string sourceDirectory,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);if(Directory.Exists(root))throw new IOException("管道验收目录已存在，拒绝覆盖。");Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var source=Path.Combine(package,"Plugins","crs.national-team","2.0.0");var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team","2.0.0");Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories)){var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;
        manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(root,"service");var fixture=Path.Combine(data,"plugins","crs.national-team","industry-pe.fixture");Directory.CreateDirectory(fixture);
        var indexFixture=Path.Combine(data,"plugins","crs.national-team","index-series.fixture");Directory.CreateDirectory(indexFixture);
        foreach(var file in Directory.EnumerateFiles(sourceDirectory,"*",SearchOption.TopDirectoryOnly))File.Copy(file,Path.Combine(indexFixture,Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(indexFixture,"synthetic-terms.txt"),"SYNTHETIC TEST ONLY - NOT A SOURCE LICENSE");
        var ranges=new[]{(1,1,1,3),(2,15,2,23),(4,4,4,6),(5,1,5,5),(6,19,6,21),(9,25,9,27),(10,1,10,7)};
        File.WriteAllText(Path.Combine(fixture,"calendar-2026.html"),"合成夹具：2026年部分节假日休市安排，周末休市。"+string.Join('。',ranges.Select(x=>$"{x.Item1}月{x.Item2}日（星期一）至{x.Item3}月{x.Item4}日（星期二）休市")));
        var pipeName="CRS.IndexSeries."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipeName);using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=deadline.Token;var passed=0;
        void Check(bool ok,string label){if(!ok)throw new InvalidDataException(label);passed++;Console.WriteLine("PASS 指数隔离管道："+label);}
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
            async Task<(BackgroundRequest Request,BackgroundResponse Status)> Collect(string job,EtfRequest payload)
            {
                var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team",job,Payload:JsonSerializer.Serialize(payload));
                Check((await Send(request)).Code=="Accepted","手动命令受理，尚非金融提交完成");BackgroundResponse status;
                do{status=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));if(status.Receipt?.State!="Completed")await Task.Delay(250,token);}while(status.Receipt?.State!="Completed");
                Check(status.Receipt?.Code=="Succeeded","沿原请求确认监督执行已提交");return(request,status);
            }
            var price=await Collect("index-series-sync",new("index-series-sync",From:new(2026,10,20),To:new(2026,10,23)));
            var valuation=await Collect("index-valuation-sync",new("index-valuation-sync",To:new(2026,10,23)));
            var reader=new WorkerEtfWorkspace(client);var report=await reader.IndexSeriesReportAsync(new(2026,10,20),new(2026,10,23),token);var revisions=await reader.IndexSeriesRevisionsAsync(new(2026,10,20),new(2026,10,23),token);
            Check(report.Rows.Count==11&&report.Rows.All(x=>x.AcceptedPriceDays==4&&x.Curve[0].NormalizedLevel==100&&x.Curve[^1].NormalizedLevel.HasValue),"真实管道分页回读11条完整后端曲线");
            Check(revisions.Count==54&&revisions.All(x=>x.State=="Accepted"&&x.EvidenceHealthy),"44日线与10估值快照具有健康原件");
            Check(report.Rows.Single(x=>x.Code=="399381").PublishedPb==1.42m&&report.Rows.All(x=>x.PbPriorSamples==0&&x.CoverageState=="Unknown")&&report.TimingProof=="NotProven","真实PB不冒充历史分位、覆盖或模型验证");
            Check(report.PriceCollectionAllowed&&report.ValuationCollectionAllowed,"仅隔离测试入口显式合成审批传递到只读报告");
            _=await Send(price.Request);var repeated=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:price.Request.RequestId));Check(repeated.Receipt?.ExecutionId==price.Status.Receipt?.ExecutionId,"同请求保持执行身份，不重复生成任务");
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health?.ProcessId==health.Health?.ProcessId&&after.Health?.State=="Running","读取前后同一健康宿主");
            // ValueTuple默认不会序列化其字段；显式投影请求和回执，避免验收文件只留下空对象。
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Transport="CapturedOfficialIndexFilesWithSyntheticApproval",Calendar="SyntheticReviewedFormatFixture",Price=new{price.Request,Receipt=price.Status.Receipt},Valuation=new{valuation.Request,Receipt=valuation.Status.Receipt},Report=report,Revisions=revisions,Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();}
    }
}
