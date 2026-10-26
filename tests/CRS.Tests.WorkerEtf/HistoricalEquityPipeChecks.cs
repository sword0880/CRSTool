using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Worker.Ipc;
using CRS.Worker.Service;

/// <summary>完整发行、监督Runner和真实管道；只回放已核公开PDF，不操作正式数据或真实源站。</summary>
internal static class HistoricalEquityPipeChecks
{
    public static async Task Run(string package,string originalPdf,string output)
    {
        package=Path.GetFullPath(package);output=Path.GetFullPath(output);if(Directory.Exists(output))throw new IOException("隔离验收目录已存在，拒绝覆盖。");
        Directory.CreateDirectory(output);StorageSecurity.Prepare(output,true);var plugins=Path.Combine(output,"Plugins");var target=Path.Combine(plugins,"crs.national-team","2.0.0");Directory.CreateDirectory(target);
        var source=Path.Combine(package,"Plugins","crs.national-team","2.0.0");
        foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {var to=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(file,to);}
        File.Copy(Path.Combine(AppContext.BaseDirectory,"CRS.Worker.EtfFixture.dll"),Path.Combine(target,"CRS.Worker.EtfFixture.dll"));
        var manifest=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(target,"plugin.json")))!;
        manifest["backgroundEntry"]=JsonSerializer.SerializeToElement(new{assembly="CRS.Worker.EtfFixture.dll",type="CRS.Worker.EtfFixture.Entry"});File.WriteAllText(Path.Combine(target,"plugin.json"),JsonSerializer.Serialize(manifest));
        var hashes=Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories).ToDictionary(x=>Path.GetRelativePath(target,x).Replace('\\','/'),x=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x))));
        var trust=Path.Combine(output,"trust.json");File.WriteAllText(trust,JsonSerializer.Serialize(new[]{new{id="crs.national-team",version="2.0.0",directory="crs.national-team/2.0.0",files=hashes}}));
        var data=Path.Combine(output,"service");var fixture=Path.Combine(data,"plugins","crs.national-team","historical-pdf.fixture");Directory.CreateDirectory(fixture);File.Copy(originalPdf,Path.Combine(fixture,"original.pdf"));
        using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=limit.Token;var pipe="CRS.HistoricalPipe."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);var reader=new WorkerEtfWorkspace(client);var passed=0;
        void Check(bool ok,string label){if(!ok)throw new InvalidDataException(label);passed++;Console.WriteLine("PASS 历史PDF完整管道："+label);}
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not("RateLimited" or "PluginBusy"))return reply;await Task.Delay(200,token);}}
        async Task<BackgroundResponse> Completed(Guid id)
        {while(true){var reply=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:id));if(reply.Receipt?.State=="Completed")return reply;await Task.Delay(200,token);}}
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var argument in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(argument);
        using var process=Process.Start(info)!;process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
        try
        {
            BackgroundResponse before;
            while(true){token.ThrowIfCancellationRequested();try{before=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(before.Health?.State=="Running")break;}catch(IOException){}await Task.Delay(200,token);}
            var documents=await reader.HistoricalEquityDocumentsAsync(token);var definition=documents.Single();
            Check(definition.OriginalHash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(originalPdf)))&&definition.ReviewedAccounts==4,"完整宿主读取固定原文目录，目录不生成持仓");
            Check(await reader.HistoricalEquityReceiptAsync(Guid.NewGuid(),token) is null,"尚未登记的业务回执为空而非成功");
            var denied=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","equity-history-import",Payload:JsonSerializer.Serialize(new EtfRequest("equity-history-import",Revision:definition.Id,Reviewer:"fixture",Reason:"fixture",Accept:true)));
            await Send(denied);Check((await Completed(denied.RequestId)).Receipt?.Code=="Rejected"&&!File.Exists(Path.Combine(fixture,"calls.log")),"未确认原文在HTTP前拒绝");
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","equity-history-import",Payload:JsonSerializer.Serialize(new EtfRequest("equity-history-import",Revision:definition.Id,Reviewer:"fixture",Reason:"captured original review",Accept:true,Original:true)));
            File.WriteAllText(Path.Combine(output,"request.json"),JsonSerializer.Serialize(request,new JsonSerializerOptions{WriteIndented=true}));
            Check((await Send(request)).Code=="Accepted","先落稳定ID，再由宿主持久受理");var done=await Completed(request.RequestId);
            var receipt=await reader.HistoricalEquityReceiptAsync(request.RequestId,token);
            Check(done.Receipt?.Code=="Succeeded"&&receipt is{Outcome:"Imported",ReviewedAccounts:4}&&receipt.OriginalHash==definition.OriginalHash,"宿主与业务回执同ID，固定原件四账户事务提交");
            var disclosures=await reader.EquityDisclosuresAsync(new("SSE","600153"),token);var events=await reader.EquityEventsAsync(new("SSE","600153"),token);
            Check(disclosures.Count==1&&disclosures[0].Holders.Count==10&&events.Count==4&&events.All(x=>x.Delta is null&&x.Nature=="Unknown"),"分页返回完整十户原表和四个未知性质快照，不确认期间交易");
            await Send(request);Check((await Completed(request.RequestId)).Receipt?.ExecutionId==done.Receipt?.ExecutionId&&File.ReadAllLines(Path.Combine(fixture,"calls.log")).Length==1,"重放原ID不重下载且不换执行身份");
            // 独立WPF进程模拟用户回查原请求，验证完整客户端和非技术界面，不读取测试服务密钥。
            var ui=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var argument in new[]{"--historical-ui",pipe,Path.Combine(output,"ui")})ui.ArgumentList.Add(argument);
            using(var child=Process.Start(ui)!){var text=child.StandardOutput.ReadToEndAsync(token);var error=child.StandardError.ReadToEndAsync(token);await child.WaitForExitAsync(token);File.WriteAllText(Path.Combine(output,"ui.log"),await text+await error);Check(child.ExitCode==0,"真实WPF历史登记入口、原请求回查与既有事实复用通过");}
            Check((await reader.EquityEventsAsync(new("SSE","600153"),token)).Count==4,"界面重复登记未新增第二份历史事实");
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health?.State=="Running"&&after.Health.ProcessId==before.Health!.ProcessId,"完整流程同一健康宿主");
            File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Transport="CapturedOfficialPdfFixture",request.RequestId,Receipt=receipt,HostReceipt=done.Receipt,Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();}
    }
}
