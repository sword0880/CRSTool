using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>候选发行、监督进程、持久命令与真实管道；仅回放已保存公开PDF，不联网或安装服务。</summary>
internal static class EtfHolderPipeChecks
{
    public static async Task Run(string package,string originals,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);
        if(Directory.Exists(root))throw new IOException("隔离验收目录必须不存在。");
        Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var source=Path.Combine(package,"Plugins","crs.national-team","2.0.0");var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team","2.0.0");Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;
        manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});
        File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(root,"service");var fixture=Path.Combine(data,"plugins","crs.national-team","etf-holders.fixture");Directory.CreateDirectory(fixture);
        foreach(var year in new[]{2024,2025})File.Copy(Path.Combine(originals,$"510300-{year}.pdf"),Path.Combine(fixture,$"510300-{year}.pdf"));
        var pipe="CRS.EtfHolders."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=limit.Token;var passed=0;
        void Check(bool ok,string message){if(!ok)throw new InvalidDataException(message);passed++;Console.WriteLine("PASS 持有人真实管道："+message);}
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not ("RateLimited" or "PluginBusy"))return reply;await Task.Delay(250,token);}}
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(arg);
        using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            BackgroundResponse health;
            while(true)
            {
                token.ThrowIfCancellationRequested();if(process.HasExited)throw new IOException("隔离宿主提前退出。");
                try{health=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(health.Health?.State=="Running")break;}catch(IOException){}
                await Task.Delay(250,token);
            }
            Check(health.Health?.State=="Running","完整候选包在隔离目录启动");
            var reader=new WorkerEtfWorkspace(client);var reports=new List<EtfHolderCandidateReport>();
            foreach(var year in new[]{2024,2025})
            {
                var url=year==2025?"https://www.sse.com.cn/disclosure/fund/announcement/c/new/2026-03-31/510300_20260331_W6CZ.pdf":"https://www.sse.com.cn/disclosure/fund/announcement/c/new/2025-03-31/510300_20250331_07Z1.pdf";
                var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","alert-manage",Payload:JsonSerializer.Serialize(new EtfRequest("alert-document",AlertDocument:new("SSE","510300",url))));
                Check((await Send(request)).Code=="Accepted","原件登记命令持久接收"+year);
                BackgroundResponse status;
                do{status=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));if(status.Receipt?.State!="Completed")await Task.Delay(250,token);}while(status.Receipt?.State!="Completed");
                Check(status.Receipt?.Code=="Succeeded","按本次命令身份核对完成"+year);
                var document=(await reader.AlertDocumentsAsync(new("SSE","510300"),token)).Single(x=>x.Url==url);
                var report=await reader.EtfHolderCandidatesAsync(new("SSE","510300"),document.Id,token);reports.Add(report);
                Check(report.Holders.Count==11&&report.State=="NeedsReview"&&report.PublishedAt is null&&report.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed),"监督Runner加载公共PDF库并传回11条待核验候选"+year);
                Check(report.Document.Hash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(originals,$"510300-{year}.pdf"))))&&report.ReportAsOf==new DateOnly(year,12,31),"原件哈希及报告期完整跨进程绑定"+year);
                _=await Send(request);var replay=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));
                Check(replay.Receipt?.ExecutionId==status.Receipt?.ExecutionId,"原命令重放保持执行身份"+year);
            }
            Check((await reader.NamedEtfHoldingsAsync(new("SSE","510300"),token)).Count==0,"候选解析不写已审核具名持仓");
            Check(File.ReadAllLines(Path.Combine(fixture,"calls.log")).Length==2,"只登记两次原件，候选查询及重放不重新采集");
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health?.ProcessId==health.Health?.ProcessId&&after.Health?.State=="Running","读取前后同一健康宿主");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="CapturedOfficialFilesFixture",Reports=reports,Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally
        {
            if(!process.HasExited)process.Kill();await process.WaitForExitAsync();
            File.WriteAllText(Path.Combine(root,"stdout.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr.log"),await stderr);
        }
    }
}
