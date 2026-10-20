using System.Diagnostics;
using System.Text.Json;
using CRS.Worker.Ipc;

internal static class PipeCompatibilityChecks
{
    public static async Task RunAsync(string package,bool includePlugins=false)
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.PipeCompatibility",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var plugins=Path.Combine(root,"empty-plugins");Directory.CreateDirectory(plugins);
        var trust=Path.Combine(root,"empty-trust.json");File.WriteAllText(trust,"[]");
        if(includePlugins){plugins=Path.Combine(Path.GetFullPath(package),"Plugins");trust=Path.Combine(Path.GetFullPath(package),"trusted-background-plugins.json");}
        var pipe="CRS.PipeCompatibility."+Guid.NewGuid().ToString("N");
        var start=new ProcessStartInfo(Path.Combine(Path.GetFullPath(package),"CRS.Worker.Service.exe"))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var value in new[]{"--console","--data",Path.Combine(root,"data"),"--plugins",plugins,"--trust",trust,"--pipe",pipe})start.ArgumentList.Add(value);
        using var process=Process.Start(start)??throw new Exception("隔离宿主未启动。");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));var client=new BackgroundPipeClient(pipe);
            while(true)
            {
                if(process.HasExited)throw new Exception("隔离宿主退出："+(await stderr));
                try
                {
                    var response=await client.QueryAsync("GetHealth",deadline.Token);
                    if(response.Code!="Ok"||response.Health?.State!="Running")throw new Exception("隔离宿主健康不通过。");
                    if(includePlugins)
                    {
                        if(response.Plugins?.Any(x=>x.PluginId=="crs.national-team"&&x.State=="Started")!=true)throw new Exception("后台插件未启动。");
                        var overview=await client.SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"QueryPlugin","crs.national-team",Payload:"{\"Operation\":\"overview\"}"),deadline.Token);
                        if(overview.Code!="Ok"||overview.Payload is null)throw new Exception("隔离概览失败："+overview.Code);
                        Console.WriteLine("PASS 原宿主与新插件包：插件Started并通过真实概览查询；仅隔离新数据库。");
                    }
                    if(!includePlugins)Console.WriteLine("PASS 旧宿主二进制兼容：替换通信契约及IPC后启动并经真实管道确认Running；未加载业务插件。");
                    Console.WriteLine(JsonSerializer.Serialize(new{ProcessId=process.Id,Data=root,Pipe=pipe,State=response.Health.State}));break;
                }
                catch(BackgroundServiceException) when(!deadline.IsCancellationRequested){await Task.Delay(100,deadline.Token);}
            }
        }
        finally
        {
            if(!process.HasExited)process.Kill(entireProcessTree:true);await process.WaitForExitAsync();
            File.WriteAllText(Path.Combine("artifacts","pipe-eof-compat-host.log"),(await stdout)+Environment.NewLine+(await stderr));
        }
    }
}
