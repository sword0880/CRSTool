using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>目录检索经过真实宿主、监督Runner和持久管道；仅回放保存的官方字节，不联网、不安装服务。</summary>
internal static class EtfReportPipeChecks
{
    public static async Task Run(string package,string sources,string originals,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);if(Directory.Exists(root))throw new IOException("隔离验收目录必须不存在。");
        Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var source=Path.Combine(package,"Plugins/crs.national-team/2.0.0");var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team/2.0.0");Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;
        manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(root,"service");var reports=Path.Combine(data,"plugins/crs.national-team/etf-reports.fixture");var holders=Path.Combine(data,"plugins/crs.national-team/etf-holders.fixture");Directory.CreateDirectory(reports);Directory.CreateDirectory(holders);
        File.Copy(Path.Combine(sources,"sse-510300-periodic-2026-direct.json"),Path.Combine(reports,"sse.json"));File.Copy(Path.Combine(sources,"szse-159915-periodic-2026-page1.json"),Path.Combine(reports,"szse.json"));
        File.Copy(Path.Combine(originals,"510300-2025.pdf"),Path.Combine(holders,"510300-2025.pdf"));
        var pipe="CRS.EtfReports."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=limit.Token;var passed=0;
        void Check(bool value,string label){if(!value)throw new InvalidDataException(label);passed++;Console.WriteLine("PASS ETF目录真实管道："+label);}
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not ("RateLimited" or "PluginBusy"))return reply;await Task.Delay(250,token);}}
        async Task<BackgroundResponse> Complete(Guid id)
        {
            while(true)
            {
                var reply=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id));
                if(reply.Receipt?.State=="Completed")return reply;
                if(reply.Receipt?.Code=="RetryDeferred")throw new InvalidDataException("隔离命令延期，不能当作成功。");
                await Task.Delay(200,token);
            }
        }
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
            Check(health.Health?.State=="Running","完整候选包及监督Runner在隔离目录启动");
            var workspace=new WorkerEtfWorkspace(client);var configuration=await workspace.ConfigurationAsync(token);
            Check(configuration.Count>=24&&configuration.Count(x=>x.Active)==5,"保留完整配置目录，启用集合只来自测试配置");
            var snapshots=new List<EtfReportIndexSnapshot>();
            foreach(var key in new[]{new EtfKey("SSE","510300"),new EtfKey("SZSE","159915")})
            {
                Check(await workspace.EtfReportIndexAsync(key,token) is{State:"NotSearched",DeclaredTotal:null},"首次读取保持未知 "+key.Market);
                var request=await workspace.QueueEtfReportsAsync(key,new(2026,1,1),new(2026,10,27),token);
                Check(request.RequestId!=Guid.Empty&&request.State is "Pending" or "Running" or "Completed" or "Accepted","实际桌面适配器返回持久请求身份，完成以回执为准 "+key.Market);
                var complete=await Complete(request.RequestId);Check(complete.Receipt?.Code=="Succeeded","按本次命令身份回读完成 "+key.Market);
                var snapshot=await workspace.EtfReportIndexAsync(key,token);snapshots.Add(snapshot);
                Check(snapshot is{State:"IndexedUnreviewed",DeclaredTotal:6,Documents.Count:6,HoldingsConfirmed:false,CoverageProof:"UnversionedDirectory"}&&snapshot.SearchId==request.RequestId.ToString("N"),"两市真实保存响应保留六条线索及未核验状态 "+key.Market);
                Check(snapshot.Documents.All(x=>x.SecurityCodes.Contains(key.Ticker))&&snapshot.Documents.All(x=>key.Market=="SSE"?x.DocumentUrl.StartsWith("https://www.sse.com.cn/disclosure/fund/",StringComparison.Ordinal):x.DocumentUrl.StartsWith("https://disc.static.szse.cn/download/disc/",StringComparison.Ordinal)),"证券和官方PDF来源跨进程绑定 "+key.Market);
                var repeated=await workspace.QueueEtfReportsAsync(key,new(2026,1,1),new(2026,10,27),token);var replay=await Complete(request.RequestId);Check(repeated.RequestId==request.RequestId&&replay.Receipt?.ExecutionId==complete.Receipt?.ExecutionId,"桌面适配器重试保持同一请求和执行身份 "+key.Market);
            }
            Check(File.ReadAllLines(Path.Combine(reports,"calls.log")).SequenceEqual(new[]{"SSE","SZSE"}),"查询和重放没有重复GET或POST");
            var sse=new EtfKey("SSE","510300");Check((await workspace.NamedEtfHoldingsAsync(sse,token)).Count==0&&(await workspace.AlertDocumentsAsync(sse,token)).Count==0,"发现报告不自动下载或批准机构持仓");
            var selected=snapshots[0].Documents.Single(x=>x.DocumentUrl.EndsWith("510300_20260331_W6CZ.pdf",StringComparison.Ordinal));
            var registration=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","alert-manage",Payload:JsonSerializer.Serialize(new EtfRequest("alert-document",AlertDocument:new("SSE","510300",selected.DocumentUrl))));
            Check((await Send(registration)).Code=="Accepted"&&(await Complete(registration.RequestId)).Receipt?.Code=="Succeeded","从目录明确选择官方原件后才登记下载");
            var document=(await workspace.AlertDocumentsAsync(sse,token)).Single();var candidates=await workspace.EtfHolderCandidatesAsync(sse,document.Id,token);
            Check(document.Url==selected.DocumentUrl&&candidates is{State:"NeedsReview",Holders.Count:11,PublishedAt:null}&&candidates.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed),"原件进入十一条待核验候选，仍不批准准确时点或持仓");
            Check((await workspace.NamedEtfHoldingsAsync(sse,token)).Count==0&&File.ReadAllLines(Path.Combine(holders,"calls.log")).SequenceEqual(new[]{"2025"}),"候选只读解析不重复下载、不写已审持仓");
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health is{State:"Running"}&&after.Health.ProcessId==health.Health!.ProcessId,"全链路前后同一健康宿主");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="CapturedOfficialResponsesAndPdf",Snapshots=snapshots,Candidates=candidates,Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally
        {
            if(!process.HasExited)process.Kill();await process.WaitForExitAsync();File.WriteAllText(Path.Combine(root,"stdout.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr.log"),await stderr);
        }
    }
}
