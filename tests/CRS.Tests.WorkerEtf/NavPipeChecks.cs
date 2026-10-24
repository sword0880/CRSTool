using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Ipc;
using CRS.Worker.Service;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>完整发行宿主、监督Runner和真实管道验收；传输使用已核对公开原件的测试夹具，不冒充实时源站。</summary>
internal static class NavPipeChecks
{
    public static async Task Run(string package,string snapshot,string output)
    {
        package=Path.GetFullPath(package);var root=Path.GetFullPath(output);
        if(Directory.Exists(root))throw new IOException("验收目录已存在，拒绝覆盖。");
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
        var data=Path.Combine(root,"service");var fixture=Path.Combine(data,"plugins","crs.national-team","nav-source.fixture");Directory.CreateDirectory(fixture);
        var names=new[]{"efunds-159915.html","efunds-159915-all.js","efunds-159915-1y.js","efunds-product-detail-overview.js"};
        for(var i=0;i<names.Length;i++)File.Copy(Path.Combine(snapshot,names[i]),Path.Combine(fixture,i+".raw"));
        var pipe="CRS.NavPipe."+Guid.NewGuid().ToString("N");var client=new BackgroundPipeClient(pipe);using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));var token=limit.Token;var passed=0;
        void Check(bool ok,string name){if(!ok)throw new InvalidDataException(name);passed++;Console.WriteLine("PASS NAV真实管道："+name);}
        async Task<BackgroundResponse> Send(BackgroundRequest request)
        {
            while(true){var reply=await client.SendAsync(request,token);if(reply.Code is not ("RateLimited" or "PluginBusy"))return reply;await Task.Delay(250,token);}
        }
        var info=new ProcessStartInfo(Path.Combine(package,"CRS.Worker.Service.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{"--console","--data",data,"--plugins",plugins,"--trust",trust,"--pipe",pipe})info.ArgumentList.Add(arg);
        using var process=Process.Start(info)!;process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
        try
        {
            BackgroundResponse health;
            while(true)
            {
                token.ThrowIfCancellationRequested();
                try{health=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));if(health.Health?.State=="Running")break;}catch(IOException){ }
                await Task.Delay(250,token);
            }
            Check(health.Health?.State=="Running","完整发行包在隔离目录启动");
            var request=new BackgroundRequest(WorkerProtocol.Version,Guid.NewGuid(),"RunNow","crs.national-team","nav-sync",Payload:JsonSerializer.Serialize(new EtfRequest("nav-sync","SZSE","159915")));
            var accepted=await Send(request);Check(accepted.Code=="Accepted","稳定NAV命令进入正式持久队列");
            BackgroundResponse status;
            do{status=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));if(status.Receipt?.State!="Completed")await Task.Delay(250,token);}while(status.Receipt?.State!="Completed");
            Check(status.Receipt?.Code=="Succeeded","监督执行完成，按本次RequestId核对成功回执");
            var reader=new WorkerEtfWorkspace(client);var rows=await reader.NavPointsAsync(new("SZSE","159915"),new(2026,10,1),new(2026,10,23),token);
            Check(rows.Count>10&&rows[^1].UnitNavYuan==2.7018m,"多页NAV经实际管道完整读取，末日单位净值正确");
            var audit=await reader.NavRevisionsAsync(new("SZSE","159915"),new(2026,10,1),new(2026,10,23),token);
            Check(audit.Count==rows.Count&&audit.All(x=>x.State=="Accepted"&&x.EvidenceHealthy),"独立修订与原件健康回读一致");
            _=await Send(request);var duplicate=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:request.RequestId));
            Check(duplicate.Receipt?.ExecutionId==status.Receipt?.ExecutionId,"同请求重放保持原执行身份");
            var after=await Send(new(WorkerProtocol.Version,Guid.NewGuid(),"GetHealth"));Check(after.Health?.ProcessId==health.Health?.ProcessId&&after.Health?.State=="Running","查询前后同一健康宿主");
            File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Transport="CapturedOfficialFilesFixture",Request=request,Receipt=status.Receipt,Rows=rows.Count,Last=rows[^1],Health=after.Health},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{if(!process.HasExited)process.Kill();await process.WaitForExitAsync();}
    }
}
