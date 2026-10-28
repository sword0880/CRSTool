using System.Security.Cryptography;
using CRS.IO;
using CRS.Worker.PluginHost;

namespace CRS.Worker.PluginRunner;

/// <summary>维护模式在普通会话租约之外运行，只有一次迁移及结束确认，不开放查询、执行或通知。</summary>
internal static class MaintenanceRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var input=Console.OpenStandardInput();using var output=Console.OpenStandardOutput();Console.SetOut(Console.Error);
        RunnerChannel? channel=null;var started=0;var completed=false;var cleanStop=false;
        try
        {
            string Option(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException();}
            var root=PhysicalPathGuard.RejectLinks(Option("--plugin-root"));var trust=PhysicalPathGuard.RejectLinks(Option("--trust-file"));var id=Option("--plugin-id");
            channel=new RunnerChannel(input,output,async(message,token)=>
            {
                if(message.Command=="Stop"&&completed){cleanStop=true;return true;}
                if(message.Command!="Migrate"||Interlocked.Exchange(ref started,1)!=0)throw new InvalidDataException("维护入口只接受一次迁移。");
                var start=RunnerChannel.Data<RunnerMaintenanceStart>(message);var context=start.Context;
                try
                {
                    if(context.PluginId!=id||context.OperationId==Guid.Empty||context.DataKey is not {Length:32}||start.ParentPid<=0
                        ||start.Nonce.Length!=64||!start.Nonce.All(char.IsAsciiHexDigit)||!CRS.Common.Integrity.ContentFingerprint.IsSha256Hex(start.PackageHash))throw new InvalidDataException("维护上下文绑定无效。");
                    var live=PhysicalPathGuard.RejectLinks(context.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
                    var snapshot=PhysicalPathGuard.RejectLinks(context.SnapshotDirectory).TrimEnd(Path.DirectorySeparatorChar);
                    if(!Directory.Exists(live)||!Directory.Exists(snapshot)||Path.GetFileName(live)!=id||Path.GetFileName(snapshot)!=id
                        ||live.Equals(snapshot,StringComparison.OrdinalIgnoreCase)||live.StartsWith(snapshot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                        ||snapshot.StartsWith(live+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("维护目录未绑定独立快照。");
                    var result=await BackgroundPluginCatalog.MigrateAsync(root,trust,start.PackageHash,context with{DataDirectory=live,SnapshotDirectory=snapshot},token).ConfigureAwait(false);
                    completed=true;
                    return new RunnerMaintenanceResult(id,start.PackageHash,start.Nonce,Environment.ProcessId,result);
                }
                finally{if(context.DataKey is not null)CryptographicOperations.ZeroMemory(context.DataKey);}
            });
            try{await channel.Completion.ConfigureAwait(false);}catch(IOException) when(cleanStop){}
            return cleanStop?0:1;
        }
        catch(Exception error){Console.Error.WriteLine("MAINTENANCE_RUNNER_FAILED:"+error.GetType().Name);return 1;}
        finally
        {
            // 父进程断连时不能留下后台迁移线程；OS关闭全部句柄后才允许恢复。
            if(started!=0&&!cleanStop)Environment.Exit(2);
            if(channel is not null)await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
