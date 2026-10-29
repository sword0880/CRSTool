using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

internal static class EquityDirectionPipeChecks
{
    public static async Task Run(string package,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);if(Directory.Exists(root))throw new IOException("隔离验收目录必须不存在。");
        Directory.CreateDirectory(root);StorageSecurity.Prepare(root,true);
        var plugins=Path.Combine(root,"Plugins");var target=Path.Combine(plugins,"crs.national-team/2.0.0");var source=Path.Combine(package,"Plugins/crs.national-team/2.0.0");Directory.CreateDirectory(target);
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;
        manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(target,f).Replace('\\','/'),f=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        var trust=Path.Combine(root,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(root,"service");var fixture=Path.Combine(data,"plugins/crs.national-team/equity-direction.fixture");Directory.CreateDirectory(fixture);File.WriteAllBytes(Path.Combine(fixture,"synthetic.pdf"),"%PDF-synthetic-direction-pipe"u8.ToArray());
        var pipe="CRS.EquityDirection."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=limit.Token;var passed=0;
        void Check(bool value,string text){if(!value)throw new InvalidDataException(text);passed++;Console.WriteLine("PASS 方向真实管道："+text);}
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not ("RateLimited" or "PluginBusy"))return reply;await Task.Delay(250,token);}}
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(argument);
        using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            BackgroundResponse before;
            while(true)
            {
                token.ThrowIfCancellationRequested();if(process.HasExited)throw new IOException("隔离宿主提前退出。");
                try{before=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(before.Health?.State=="Running")break;}catch(IOException){}
                await Task.Delay(250,token);
            }
            Check(before.Health?.State=="Running","完整候选宿主在独立目录启动");
            var assemblyReply=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:JsonSerializer.Serialize(new{Operation="fixture-assemblies"})));
            var assemblies=JsonSerializer.Deserialize<string[]>(assemblyReply.Payload??"[]")!;
            Check(assemblies.Contains("CRS.Worker.PluginRunner")&&!assemblies.Contains("CRS.Worker.Service"),"后台实际位于监督Runner进程，不在服务进程内执行");
            var workspace=new WorkerEtfWorkspace(client);var period=new DateOnly(2026,6,30);var initial=await workspace.EquityDirectionAsync(period,token);
            Check(initial is{DisclosedSecurities:1,ReviewedAccounts:2,ClassifiedSecurities:0}&&initial.Groups.All(x=>x.SampleWeightPercent is null),"实际桌面适配器返回未归类样本，不捏造方向");
            var position=initial.Positions.First();var review=new EquityDirectionReview(position.Holding.DisclosureId,position.Holding.EvidenceHash!,"合成标准","2026","J","合成银行",null,"合成P1","synthetic-pipe-reviewer","真实管道隔离审核",true,10m,period,"合成P2");
            await workspace.ReviewEquityDirectionAsync(review,token);var report=await workspace.EquityDirectionAsync(period,token);var fact=report.Positions.First().Fact!;var id=Guid.ParseExact(fact.ReviewId,"N");
            var receipt=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id));
            Check(receipt.Receipt is{State:"Completed",Code:"Succeeded",ExecutionId:not null},"界面提交的请求身份与持久业务核验相同，隔离宿主回执已完成");
            var execution=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetExecution",ExecutionId:receipt.Receipt!.ExecutionId));
            Check(execution.Code=="Ok"&&execution.Execution?.Result=="Succeeded","同一次请求取得持久执行结果");
            Check(report.ClassifiedSecurities==1&&report.PricedSecurities==1&&report.Groups.All(x=>x.SampleWeightPercent==100m)&&!report.WholePortfolioProven,"完整跨进程报告只计算已核样本，机构各自独立分母");
            var replay=new BackgroundRequest(WorkerProtocol.Version,id,"RunNow","crs.national-team","equity-direction-review",Payload:JsonSerializer.Serialize(new EtfRequest("equity-direction-review",EquityDirectionReview:review)));
            await Send(replay);var replayReceipt=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id));
            Check(replayReceipt.Receipt?.ExecutionId==receipt.Receipt.ExecutionId&&(await workspace.EquityDirectionAsync(period,token)).Positions.All(x=>x.Fact?.ReviewId==fact.ReviewId),"同请求重放不新增执行或方向核验版本");
            var rejected=false;try{await workspace.ReviewEquityDirectionAsync(review with{SectorName="无基线覆盖"},token);}catch(InvalidDataException){rejected=true;}
            Check(rejected&&(await workspace.EquityDirectionAsync(period,token)).Positions.All(x=>x.Fact?.ReviewId==fact.ReviewId),"陈旧窗口不能越过基线覆盖已提交方向");
            Check(Directory.GetFiles(Path.Combine(data,"plugins/crs.national-team/raw"),"*.raw").Length==2,"读取与方向核验没有下载或新增原件");
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health?.State=="Running"&&after.Health.ProcessId==before.Health!.ProcessId,"前后同一健康宿主");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticDirectEquityOnly",Receipt=receipt.Receipt,Execution=execution.Execution,Report=report,Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();File.WriteAllText(Path.Combine(root,"stdout.log"),await stdout);File.WriteAllText(Path.Combine(root,"stderr.log"),await stderr);}
    }
}
