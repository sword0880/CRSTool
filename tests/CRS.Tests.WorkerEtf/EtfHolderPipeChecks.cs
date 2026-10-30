using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>候选发行、监督进程、持久命令与真实管道；仅回放已保存公开PDF，不联网或安装服务。</summary>
internal static class EtfHolderPipeChecks
{
    public static async Task Run(string package,string originals,string output,bool efund=false,bool publication=false,bool recovery=false,bool harvest=false,bool sourceBudget=false,bool isolateManagement=false,string? otherMarketOriginals=null)
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
        var key=harvest?new EtfKey("SZSE","159919"):efund?new EtfKey("SZSE","159915"):new EtfKey("SSE","510300");
        (string File,string Url,DateOnly AsOf,bool Named)[] captures=publication?[]:harvest?
            [("159919-2024",HarvestCsi300HolderPdf.Annual2024Url,new(2024,12,31),true),("159919-2025",HarvestCsi300HolderPdf.Annual2025Url,new(2025,12,31),true)]:efund?
            [("159915-2024",EfundChiNextHolderPdf.Annual2024Url,new(2024,12,31),true),("159915-2025",EfundChiNextHolderPdf.Annual2025Url,new(2025,12,31),true),
             ("159915-2026H1",EfundChiNextHolderPdf.Interim2026Url,new(2026,6,30),false)]:
            [("510300-2024","https://www.sse.com.cn/disclosure/fund/announcement/c/new/2025-03-31/510300_20250331_07Z1.pdf",new(2024,12,31),true),
             ("510300-2025","https://www.sse.com.cn/disclosure/fund/announcement/c/new/2026-03-31/510300_20260331_W6CZ.pdf",new(2025,12,31),true)];
        foreach(var capture in captures)File.Copy(Path.Combine(originals,capture.File+".pdf"),Path.Combine(fixture,capture.File+".pdf"));
        if(isolateManagement)File.Copy(Path.Combine(otherMarketOriginals??throw new ArgumentException("需要深市原件目录。"),"159919-2024.pdf"),Path.Combine(fixture,"159919-2024.pdf"));
        if(publication)File.WriteAllBytes(Path.Combine(fixture,"publication-synthetic.pdf"),"%PDF-synthetic-publication-time"u8.ToArray());
        if(recovery)File.WriteAllText(Path.Combine(fixture,sourceBudget?"source-cooldown-once.fixture":"source-queue-once.fixture"),"synthetic source budget unavailable once");
        if(isolateManagement)File.WriteAllText(Path.Combine(fixture,"commit-hold.fixture"),"synthetic pause after business commit");
        var pipe="CRS.EtfHolders."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=limit.Token;var passed=0;
        void Check(bool ok,string message){if(!ok)throw new InvalidDataException(message);passed++;Console.WriteLine("PASS 持有人真实管道："+message);}
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not ("RateLimited" or "PluginBusy"))return reply;await Task.Delay(250,token);}}
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(arg);
        var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
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
            var reader=new WorkerEtfWorkspace(client);var reports=new List<EtfHolderCandidateReport>();EtfHolderCandidateReport? otherMarketReport=null;
            foreach(var capture in captures)
            {
                var url=capture.Url;var label=capture.File;
                var jobId=isolateManagement?EtfDocumentCollectionProtocol.Job(key.Market):"alert-manage";
                var operation=isolateManagement?EtfDocumentCollectionProtocol.Operation:"alert-document";
                var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team",jobId,Payload:JsonSerializer.Serialize(new EtfRequest(operation,AlertDocument:new(key.Market,key.Ticker,url))));
                Check((await Send(request)).Code=="Accepted","原件登记命令持久接收"+label);
                BackgroundResponse status;
                if(recovery&&reports.Count==0)
                {
                    do{status=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));if(status.Receipt?.Code!="RetryDeferred")await Task.Delay(250,token);}while(status.Receipt?.Code!="RetryDeferred");
                    Check(status.Receipt is{State:"Pending",Code:"RetryDeferred"},"Runner传回来源退避，原命令保持Pending而非Failed");
                    var deferred=(await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHistory","crs.national-team"))).History!.Single(x=>x.JobId==jobId);
                    Check(deferred.Result=="RetryDeferred"&&deferred.Checkpoint!.StartsWith(sourceBudget?"alert=DocumentSourceCooldown;":"alert=DocumentSourceQueueFull;"),"真实执行保留具名来源原因，不丢成OperationFailed");
                    var plan=(await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",PageSize:10))).TaskActivity!.Rows.Single(x=>x.RequestId==request.RequestId);
                    Check(plan.NotBefore>CRS.Timing.BeijingClock.Now&&(await reader.AlertDocumentsAsync(key,token)).Count==0,"宿主持久等待时间，失败不登记原件");
                    if(sourceBudget)Check(plan.NotBefore<CRS.Timing.BeijingClock.Now.AddSeconds(40),"明确三十五秒来源截止跨Runner后不扩大为三十分钟");
                    if(isolateManagement)
                    {
                        var settings=await reader.AlertConfigurationAsync(token);
                        await reader.SaveAlertConfigurationAsync(settings with{Rules=settings.Rules with{Version="document-collection-pipe",Enabled=false}},token);
                        Check((await reader.AlertConfigurationAsync(token)).Rules is{Version:"document-collection-pipe",Enabled:false},"沪市原件仍Pending时管理命令真实受理并完成，不是AlreadyRunning");
                        var otherKey=new EtfKey("SZSE","159919");
                        await reader.RegisterAlertDocumentAsync(new(otherKey.Market,otherKey.Ticker,HarvestCsi300HolderPdf.Annual2024Url),token);
                        var otherDocument=(await reader.AlertDocumentsAsync(otherKey,token)).Single();
                        otherMarketReport=await reader.EtfHolderCandidatesAsync(otherKey,otherDocument.Id,token);
                        Check(otherMarketReport is{State:"NeedsReview",Holders.Count:11}&&otherMarketReport.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed),"沪市原件仍Pending时深市独立任务已完成完整候选，不批准持仓");
                        var unchanged=(await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",PageSize:10))).TaskActivity!.Rows.Single(x=>x.RequestId==request.RequestId);
                        Check(unchanged.NotBefore==plan.NotBefore&&unchanged.Reason=="RetryDeferred","其它市场和管理完成不清空或延长原沪市等待期限");
                        Check((await reader.NamedEtfHoldingsAsync(otherKey,token)).Count==0,"深市原件仍与正式具名持仓隔离");
                    }
                    File.WriteAllText(Path.Combine(root,"deferred-before-restart.json"),JsonSerializer.Serialize(new{Request=request,Status=status,Execution=deferred,Plan=plan}));
                    var oldPid=process.Id;process.Kill();await process.WaitForExitAsync();
                    File.WriteAllText(Path.Combine(root,"stdout-before-restart.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr-before-restart.log"),await stderr);process.Dispose();
                    process=Process.Start(info)!;stdout=process.StandardOutput.ReadToEndAsync();stderr=process.StandardError.ReadToEndAsync();
                    while(true)
                    {
                        token.ThrowIfCancellationRequested();if(process.HasExited)throw new IOException("恢复宿主提前退出。");
                        try{health=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(health.Health?.State=="Running")break;}catch(IOException){}
                        await Task.Delay(250,token);
                    }
                    reader=new WorkerEtfWorkspace(client);
                    var restored=(await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetTaskActivity",PageSize:10))).TaskActivity!.Rows.Single(x=>x.RequestId==request.RequestId);
                    Check(process.Id!=oldPid&&restored.NotBefore==plan.NotBefore,"宿主重启保留原来源等待预算");
                    var replayed=await Send(request);
                    Check(replayed.Receipt is{State:"Pending",Code:"RetryDeferred"},"原请求重放沿原ID等待，不重建工作或立即再下载");
                }
                var committedRestart=false;
                do
                {
                    status=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));
                    if(isolateManagement&&reports.Count==0&&!committedRestart&&File.Exists(Path.Combine(fixture,"commit-hold.fixture.reached")))
                    {
                        var saved=(await reader.AlertDocumentsAsync(key,token)).Single();
                        var callsBefore=File.ReadAllLines(Path.Combine(fixture,"calls.log")).Length;
                        Check(status.Receipt?.State=="Running"&&callsBefore==2,"原件业务已提交、宿主回执仍Running的窗口真实可见");
                        File.WriteAllText(Path.Combine(root,"committed-before-restart.json"),JsonSerializer.Serialize(new{Request=request,Status=status,Document=saved,SuccessfulFetches=callsBefore}));
                        var oldPid=process.Id;process.Kill();await process.WaitForExitAsync();
                        File.WriteAllText(Path.Combine(root,"stdout-before-commit-restart.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr-before-commit-restart.log"),await stderr);process.Dispose();
                        process=Process.Start(info)!;stdout=process.StandardOutput.ReadToEndAsync();stderr=process.StandardError.ReadToEndAsync();
                        while(true)
                        {
                            token.ThrowIfCancellationRequested();if(process.HasExited)throw new IOException("提交后恢复宿主提前退出。");
                            try{health=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(health.Health?.State=="Running")break;}catch(IOException){}
                            await Task.Delay(250,token);
                        }
                        reader=new WorkerEtfWorkspace(client);committedRestart=true;
                        Check(process.Id!=oldPid&&(await reader.AlertDocumentsAsync(key,token)).Single()==saved,"提交后真实宿主重启保留原件身份、摘要和首见时间");
                    }
                    if(status.Receipt?.State!="Completed")await Task.Delay(250,token);
                }while(status.Receipt?.State!="Completed");
                if(committedRestart)
                {
                    Check(File.ReadAllLines(Path.Combine(fixture,"calls.log")).Length==2,"控制库中断恢复沿原ID完成，不重复下载已提交原件");
                    var resumed=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:status.Receipt?.ExecutionId));
                    Check(resumed.Execution is{Result:"Succeeded"}&&resumed.Execution.Checkpoint!.Contains("reusedCommitted=True"),"恢复执行明确记录复用已提交业务身份");
                }
                Check(status.Receipt?.Code=="Succeeded","按本次命令身份核对完成"+label);
                var document=(await reader.AlertDocumentsAsync(key,token)).Single(x=>x.Url==url);
                var report=await reader.EtfHolderCandidatesAsync(key,document.Id,token);reports.Add(report);
                Check((capture.Named?report is{Holders.Count:11,State:"NeedsReview"}:report is{Holders.Count:0,State:"NotDisclosed",Scope:"AggregateOnlyNoNamedTable"})
                    &&report.PublishedAt is null&&report.Holders.All(x=>!x.OriginalConfirmed&&!x.IndependentOwnershipConfirmed),"监督Runner保留候选或未披露状态，不自动确认"+label);
                Check(report.Document.Hash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(originals,capture.File+".pdf"))))&&report.ReportAsOf==capture.AsOf,"原件哈希及报告期完整跨进程绑定"+label);
                if(harvest)
                {
                    Check(report.ParserVersion==HarvestCsi300HolderPdf.Version&&report.Holders[0].RegisteredHolder=="中央汇金投资有限责任公司"
                        &&report.Holders[1].RegisteredHolder=="中央汇金资产管理有限责任公司"&&report.Holders[^1].RegisteredHolder.EndsWith("联接基金(LOF)",StringComparison.Ordinal),"真实管道保留汇金两个登记法人和完整联接基金名称"+label);
                    Check(capture.AsOf.Year==2024?report.Holders[^1].EvidencePage.Contains("62至63")&&report.Holders[^1].RawHolder.Contains('\n'):
                        report.Holders[2].EvidencePage.Contains("66页")&&report.Holders[3].EvidencePage.Contains("67页")&&report.Holders[^1].EvidencePage.Contains("67页"),"实际跨页证据位置完整传回桌面客户端"+label);
                }
                _=await Send(request);var replay=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));
                Check(replay.Receipt?.ExecutionId==status.Receipt?.ExecutionId,"原命令重放保持执行身份"+label);
            }
            Check((await reader.NamedEtfHoldingsAsync(key,token)).Count==0,"候选解析不写已审核具名持仓");
            if(captures.Length>0)Check(File.ReadAllLines(Path.Combine(fixture,"calls.log")).Length==captures.Length+(isolateManagement?1:0),"只登记明确原件，候选查询及重放不重新采集");
            NamedEtfHolding? publicationHolding=null;
            if(publication)
            {
                const string syntheticUrl="https://www.sse.com.cn/synthetic-publication-time.pdf";
                var register=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","alert-manage",Payload:EtfBusinessProtocol.WriteRequest(new("alert-document",AlertDocument:new(key.Market,key.Ticker,syntheticUrl))));
                Check((await Send(register)).Code=="Accepted","合成时间精度原件登记持久受理");
                BackgroundResponse registered;
                do{registered=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:register.RequestId));if(registered.Receipt?.State!="Completed")await Task.Delay(250,token);}while(registered.Receipt?.State!="Completed");
                Check(registered.Receipt?.Code=="Succeeded","合成原件登记已完成，不把排队当成功");
                var document=(await reader.AlertDocumentsAsync(key,token)).Single();
                var originalEvents=await reader.AlertEventsAsync(new(),token);var originalOutbox=await reader.AlertOutboxAsync(token);
                var dated=new NamedEtfHoldingReview(document.Id,"synthetic-publication-legal","合成日期披露法人","合成日期账户",new(2025,12,31),null,1,"合成P1","synthetic-publication-reviewer",true,true)
                    {PublicationDate=new(2026,3,31),PublicationPrecision=EtfPublicationPrecision.DateOnly};
                await reader.ReviewNamedEtfHoldingAsync(dated,token);publicationHolding=(await reader.NamedEtfHoldingsAsync(key,token)).Single();
                Check(publicationHolding.PublishedAt is null&&publicationHolding.PublicationDate==dated.PublicationDate&&publicationHolding.PublicationPrecision==EtfPublicationPrecision.DateOnly,"真实客户端及监督Runner保存日期精度，准确时刻保持空值");
                Check(publicationHolding.FirstSeenAt==DateTimeOffset.Parse(document.FirstSeenAt)&&publicationHolding.PublicationText.Contains("仅日期"),"首见由已登记原件固定，显示明确区分日期与时刻");
                var downgrade=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","alert-manage",Payload:EtfBusinessProtocol.WriteRequest(new("alert-holding-review",NamedEtfHolding:dated)));
                _=await Send(downgrade);BackgroundResponse denied;
                do{denied=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:downgrade.RequestId));if(denied.Receipt?.State!="Completed")await Task.Delay(250,token);}while(denied.Receipt?.State!="Completed");
                Check(denied.Receipt?.Code=="Rejected"&&(await reader.NamedEtfHoldingsAsync(key,token)).Count==1,"实际旧操作拒绝日期载荷，不覆盖或重复写入");
                Check(File.ReadAllLines(Path.Combine(fixture,"calls.log")).Length==1,"核验和回读不重复下载合成原件");
                var afterEvents=await reader.AlertEventsAsync(new(),token);var afterOutbox=await reader.AlertOutboxAsync(token);
                Check(afterEvents.All(x=>x.Lane==AlertLane.DataQuality)&&afterEvents.Select(x=>x.Id).ToHashSet().SetEquals(originalEvents.Select(x=>x.Id))
                    &&afterOutbox.Select(x=>x.Id).ToHashSet().SetEquals(originalOutbox.Select(x=>x.Id))&&afterOutbox.All(x=>x.Attempts==0&&x.AcceptedAt is null),
                    "日期核验沿用既有数据核查，不新增金融事件、不重复排队或投递通知");
            }
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health?.ProcessId==health.Health?.ProcessId&&after.Health?.State=="Running","读取前后同一健康宿主");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,RecoveryAcrossHostRestart=recovery,IsolatedDocumentCollection=isolateManagement,Source=publication?"SyntheticPublicationDateReview":"CapturedOfficialFilesFixture",Reports=reports,OtherMarketReport=otherMarketReport,PublicationHolding=publicationHolding,Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally
        {
            if(!process.HasExited)process.Kill();await process.WaitForExitAsync();
            File.WriteAllText(Path.Combine(root,"stdout.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr.log"),await stderr);
            process.Dispose();
        }
    }
}
