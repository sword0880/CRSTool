using System.Diagnostics;
using System.Security.Cryptography;
using CRS.Worker.Abstractions;

namespace CRS.Worker.PluginHost;

/// <summary>一次维护对应一个独立受限进程；专用期限结束即回收进程，不把结果未知当作完成。</summary>
public static class IsolatedStorageMigration
{
    public static async Task<BackgroundStorageMigrationResult> RunAsync(string runnerPath,string root,string trustFile,TrustedPackage package,
        BackgroundStorageMigrationContext migration,TimeSpan timeout,CancellationToken token)
    {
        if(timeout<TimeSpan.FromSeconds(1)||timeout>TimeSpan.FromMinutes(10)||migration.PluginId!=package.Id||migration.DataKey is not {Length:32})throw new ArgumentException("维护期限或插件绑定无效。");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout);
        var info=new ProcessStartInfo(Path.GetFullPath(runnerPath)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(runnerPath))!};
        foreach(var value in new[]{"--maintenance","--plugin-root",Path.GetFullPath(root),"--trust-file",Path.GetFullPath(trustFile),"--plugin-id",package.Id})info.ArgumentList.Add(value);
        using var process=Process.Start(info)??throw new IOException("维护Runner无法启动。");
        RunnerProcessJob? job=null;RunnerChannel? channel=null;
        var key=migration.DataKey.ToArray();
        try
        {
            job=new RunnerProcessJob(process);
            _=DrainAsync(process.StandardError);
            channel=new RunnerChannel(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,(_,_)=>throw new InvalidDataException("维护Runner不能调用宿主业务能力。"));
            var hash=RunnerChannel.PackageHash(package);var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var response=await channel.CallAsync<RunnerMaintenanceResult>("Migrate",new RunnerMaintenanceStart(hash,nonce,Environment.ProcessId,migration with{DataKey=key}),deadline.Token).ConfigureAwait(false);
            var result=response.Result;
            if(response.PluginId!=package.Id||response.PackageHash!=hash||response.Nonce!=nonce||response.Pid!=process.Id
                ||result.MigrationId!=migration.MigrationId||result.OperationId!=migration.OperationId||result.State is not ("Completed" or "AlreadyCompleted")
                ||result.ConvertedDatabases is <0 or >64||!CRS.Common.Integrity.ContentFingerprint.IsSha256Hex(result.EvidenceSha256))throw new InvalidDataException("维护回执未绑定本次进程和操作。");
            if(!await channel.CallAsync<bool>("Stop",true,deadline.Token).ConfigureAwait(false))throw new InvalidDataException("维护Runner未确认结束。");
            await channel.DisposeAsync().ConfigureAwait(false);channel=null;
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if(process.ExitCode!=0)throw new IOException("维护Runner非零结束，结果尚未确认。");
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            // 必须确认旧进程退出，才能由上层打开普通插件或重试迁移。
            job?.Dispose();
            try{if(!process.HasExited)process.Kill(entireProcessTree:true);}catch(InvalidOperationException) when(process.HasExited){}
            using var ending=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(ending.Token).ConfigureAwait(false);
            if(channel is not null)await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
    private static async Task DrainAsync(StreamReader reader)
    {try{var buffer=new char[1024];while(await reader.ReadAsync(buffer).ConfigureAwait(false)>0){}}catch(IOException){}catch(ObjectDisposedException){}}
}
