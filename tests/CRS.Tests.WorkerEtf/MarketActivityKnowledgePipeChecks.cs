using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

internal static class MarketActivityKnowledgePipeChecks
{
    public static async Task Run(string package,string output,bool jointStudy=false,bool namedHistory=false)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);if(Directory.Exists(root))throw new IOException("隔离验收目录必须不存在。");Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team/2.0.0");Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(Path.Combine(package,"Plugins/crs.national-team/2.0.0"),"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(Path.Combine(package,"Plugins/crs.national-team/2.0.0"),file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(root,"service");Directory.CreateDirectory(Path.Combine(data,"plugins/crs.national-team/market-knowledge.fixture"));
        if(namedHistory)Directory.CreateDirectory(Path.Combine(data,"plugins/crs.national-team/named-holding-history.fixture"));
        var pipe="CRS.ActivityKnowledge."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=deadline.Token;var passed=0;
        void Check(bool value,string text){if(!value)throw new InvalidDataException(text);passed++;Console.WriteLine("PASS 成交知识真实管道："+text);}
        async Task<BackgroundResponse> Send(BackgroundRequest request){while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not ("RateLimited" or "PluginBusy"))return reply;await Task.Delay(250,token);}}
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(argument);
        using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            BackgroundResponse before;while(true){token.ThrowIfCancellationRequested();if(process.HasExited)throw new IOException("隔离宿主提前退出。");try{before=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(before.Health?.State=="Running")break;}catch(IOException){}await Task.Delay(250,token);}
            Check(before.Health?.State=="Running","完整候选宿主在独立目录启动");
            var assemblyReply=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:JsonSerializer.Serialize(new{Operation="fixture-assemblies"})));var assemblies=JsonSerializer.Deserialize<string[]>(assemblyReply.Payload??"[]")!;
            Check(assemblies.Contains("CRS.Worker.PluginRunner")&&!assemblies.Contains("CRS.Worker.Service"),"业务在监督Runner中执行");
            var workspace=new WorkerEtfWorkspace(client);var day=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime).AddDays(-7);var initial=await workspace.MarketActivityKnowledgeAsync(day,day,null,token);
            Check(initial.Revisions.Count==3&&initial.Revisions.Count(x=>x.State==MarketCycleObservationState.NeedsReview)==2,"实际客户端完整读取两市原件与待审冲突");
            Check(initial.Cutoff.Offset==TimeSpan.FromHours(8)&&initial.Revisions.All(x=>x.FirstSeenAt.Offset==TimeSpan.FromHours(8)&&x.StateRecordedAt.Offset==TimeSpan.FromHours(8)),"完整分块传输后的系统时刻仍为北京时间");
            var targetRow=initial.Revisions.Single(x=>x.Market=="SSE"&&x.AmountYuan==101);var earlier=await workspace.MarketActivityKnowledgeAsync(day,day,targetRow.FirstSeenAt.AddTicks(-1),token);
            var cycleBefore=await workspace.MarketCycleStudyAsync(day,initial.Cutoff,token);
            Check(cycleBefore.Assessment.DataStatus==MarketCycleDataStatus.InsufficientData&&cycleBefore.Input.ActivityPoints.Single() is{AmountYuan:null,State:MarketCycleObservationState.NeedsReview}&&!cycleBefore.NotificationsEnabled,"周期查询跨监督进程实际调用评估器，成交冲突和缺失输入关闭信号");
            Check(earlier.Revisions.Count==2&&earlier.Revisions.All(x=>x.State==MarketCycleObservationState.Accepted),"真实登记时点之间的截止只包含当时可知版本");
            await workspace.ReviewMarketActivityAsync(targetRow.Id,true,"synthetic-pipe-reviewer",token);var after=await workspace.MarketActivityKnowledgeAsync(day,day,null,token);var selected=after.Revisions.Single(x=>x.Id==targetRow.Id);
            Check(selected.State==MarketCycleObservationState.Accepted&&after.Revisions.Single(x=>x.Market=="SSE"&&x.AmountYuan==100).State==MarketCycleObservationState.Rejected,"人工接受和竞争否决跨进程同事务生效");
            var id=Guid.ParseExact(selected.StateCommandId!,"N");var receipt=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id));
            Check(receipt.Receipt is{State:"Completed",Code:"Succeeded",ExecutionId:not null},"事件关联请求与隔离宿主持久回执一致");
            var execution=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:receipt.Receipt!.ExecutionId));Check(execution.Execution?.Result=="Succeeded","同请求的持久执行结果已回读");
            Check((await workspace.MarketActivityKnowledgeAsync(day,day,initial.Cutoff,token)).Fingerprint==initial.Fingerprint,"后来的人工审核不改变早先截止的金融指纹");
            var replay=new BackgroundRequest(WorkerProtocol.Version,id,"RunNow","crs.national-team","activity-review",Payload:JsonSerializer.Serialize(new EtfRequest("activity-review",Revision:targetRow.Id,Accept:true,Reviewer:"synthetic-pipe-reviewer")));
            await Send(replay);var same=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id));
            Check(same.Receipt?.ExecutionId==receipt.Receipt.ExecutionId&&(await workspace.MarketActivityKnowledgeAsync(day,day,null,token)).Revisions.Single(x=>x.Id==targetRow.Id).StateEventHash==selected.StateEventHash,"重放原请求不新增审核事件或执行身份");
            Check((await workspace.MarketActivityAsync(token)).Single(x=>x.Date==day).AmountYuan==301m,"既有市场冷热度仍按已核两市量额汇总");
            var cycleAfter=await workspace.MarketCycleStudyAsync(day,null,token);
            Check(cycleAfter.Input.ActivityPoints.Single().AmountYuan==301m&&cycleAfter.Sources.Count(x=>x.Component=="Activity")==2,"真实周期客户端完整复算冻结报告，采用审核后的两市合计并保留各原件");
            JointExitStudyReport? joint=null;
            if(jointStudy)
            {
                var countBefore=(await workspace.AlertEventsAsync(new(),token)).Count;
                joint=await workspace.JointExitStudyAsync(new("SSE","510300"),day,token);
                Check(joint.State=="Unavailable"&&!joint.DataReady&&joint.Components.Count==5&&!joint.NotificationsEnabled,"联合查询跨Service和Runner保留五类缺口，通知保持关闭");
                Check(joint.Input.Cycle?.AmountRatioToPrior20Mean is null&&joint.Input.Cycle?.DataStatus==MarketCycleDataStatus.InsufficientData,"两市单日成交已核也不冒充周期历史充分");
                Check(joint.Input.Configuration.Ticker=="510300"&&joint.Input.AsOfDate==day&&joint.Input.SnapshotCutoff.Offset==TimeSpan.FromHours(8),"当前配置ETF、统计日和北京快照身份完整回读");
                Check(JointExitStudy.Evaluate(joint.Input,joint.Rules).Fingerprint==joint.Fingerprint,"真实客户端分块传输后的联合输入与输出摘要可复算");
                Check((await workspace.AlertEventsAsync(new(),token)).Count==countBefore,"真实联合查询不新增预警事件");
            }
            Check((await workspace.MarketCycleStudyAsync(day,initial.Cutoff,token)).Fingerprint==cycleBefore.Fingerprint,"真实管道中后来的审核不改写旧截止周期输入");
            NamedEtfHoldingHistoryReport? named=null,damagedNamed=null;
            if(namedHistory)
            {
                named=await workspace.EtfHoldingHistoryAsync(new("SSE","510300"),day,token);
                Check(named.Baseline.Holdings.Count==5&&named.Rows.Single(x=>x.ReportAsOf==named.Baseline.ReportAsOf&&x.InstitutionGroup=="汇金投资").Delta==-10,"具名多期持仓真实跨Service和Runner返回同账户相邻披露差额");
                Check(named.Rows.Single(x=>x.ReportAsOf==named.Baseline.ReportAsOf&&x.InstitutionGroup=="外汇局投资平台").Delta is null,"账户缺失一期再出现，跨进程仍不跨缺口计算差额");
                Check(named.Families.Single(x=>x.Family=="国家大基金").LatestDisclosedAccounts==3&&!named.WholePortfolioProven,"三期大基金法人分别保留，只证明披露子集");
                Check(named.Rows.Any(x=>x.LegalName=="合成私募法人")&&named.Baseline.Holdings.All(x=>x.LegalName!="合成私募法人")&&named.Baseline.Exclusions.Any(x=>x.Reason=="InstitutionIdentityUnresolved"),"目录外具名持有人跨进程仍保留记录及排除原因，不进入国家队基准");
                Check(NamedEtfHoldingHistory.Evaluate(named.Input).Fingerprint==named.Fingerprint&&named.Input.SnapshotCutoff.Offset==TimeSpan.FromHours(8),"多期记录完整分块回读、北京快照和规范指纹复算一致");
                var latestHash=named.Baseline.Holdings[0].DocumentHash;File.WriteAllText(Path.Combine(data,"plugins/crs.national-team/raw",latestHash+".raw"),"damaged synthetic latest original");
                damagedNamed=await workspace.EtfHoldingHistoryAsync(new("SSE","510300"),day,token);
                Check(damagedNamed.Baseline is{State:"LatestBaselineUnavailable",Holdings.Count:0},"真实管道重新读取现场损坏原件后关闭最新基准，不退旧期");
                Check((await workspace.AlertEventsAsync(new(),token)).Count==0&&(await workspace.AlertOutboxAsync(token)).Count==0,"持仓历史读取与损坏处理没有发出金融事件或通知");
            }
            Check(Directory.GetFiles(Path.Combine(data,"plugins/crs.national-team/raw"),"*.raw").Length==(namedHistory?6:3),"查询和审核没有联网或新增原件");
            var health=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(health.Health?.State=="Running"&&health.Health.ProcessId==before.Health!.ProcessId,"前后同一健康宿主");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source=namedHistory?"SyntheticActivityAndNamedEtfHistory":jointStudy?"SyntheticActivityAndIncompleteJointStudy":"SyntheticMarketActivityKnowledgeAndCycleStudy",Initial=initial,Earlier=earlier,After=after,CycleBefore=cycleBefore,CycleAfter=cycleAfter,Joint=joint,NamedHistory=named,DamagedNamedHistory=damagedNamed,Receipt=receipt.Receipt,Execution=execution.Execution,Health=health.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();File.WriteAllText(Path.Combine(root,"stdout.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr.log"),await stderr);}
    }
}
