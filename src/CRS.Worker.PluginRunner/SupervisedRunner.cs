using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.PluginHost;

namespace CRS.Worker.PluginRunner;

internal static class SupervisedRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var input=Console.OpenStandardInput();using var output=Console.OpenStandardOutput();Console.SetOut(Console.Error);
        BackgroundPluginCatalog? catalog=null;RunnerContext? context=null;RunnerChannel? channel=null;BufferedRunnerDiagnostics? diagnostics=null;FileStream? ownership=null;var started=0;var cleanStop=false;
        try
        {
            string Option(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException();}
            var root=Path.GetFullPath(Option("--plugin-root"));var trust=Path.GetFullPath(Option("--trust-file"));var id=Option("--plugin-id");
            if(!File.Exists(trust)||new FileInfo(trust).Length>1024*1024)throw new InvalidDataException();
            var packages=JsonSerializer.Deserialize<TrustedPackage[]>(await File.ReadAllTextAsync(trust),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException();
            var package=packages.Single(x=>x.Id==id);var hash=RunnerChannel.PackageHash(package);
            // 先建立协议循环，收到宿主启动材料后才加载业务DLL；父进程可先绑定Job Object。
            channel=new RunnerChannel(input,output,async(message,token)=>
            {
                if(message.Command=="Start")
                {
                    if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidDataException("Repeated Runner start.");
                    var start=RunnerChannel.Data<RunnerStart>(message);
                    try
                    {
                        if(start.PluginId!=id||start.PackageHash!=hash||start.Nonce.Length!=64||!start.Nonce.All(Uri.IsHexDigit)
                            ||start.ParentPid<=0||start.DataKey is not null&&start.DataKey.Length!=32)throw new InvalidDataException();
                        if(start.DataDirectory is not null&&start.DataKey is {Length:32})
                        {
                            var directory=Path.GetFullPath(start.DataDirectory);
                            ownership=await AcquireOwnershipAsync(directory,token).ConfigureAwait(false);
                            context=new(id,directory,start.DataKey,channel!);
                        }
                        diagnostics=new BufferedRunnerDiagnostics(async(value,cancellation)=>{_=await channel!.CallAsync<bool>("Lifecycle",value,cancellation).ConfigureAwait(false);});diagnostics.Start();
                        catalog=new BackgroundPluginCatalog(onUnresponsive:()=>Environment.Exit(2),hostContext:_=>context??throw new InvalidDataException(),
                            lifecycleDiagnostic:(_,phase,code)=>diagnostics.Report(phase,code));
                        await catalog.LoadSingleAsync(root,trust,id,token).ConfigureAwait(false);
                        if(catalog.Statuses.SingleOrDefault(x=>x.PluginId==id)?.State!="Started")throw new InvalidDataException("Runner plugin startup failed.");
                        var jobs=catalog.Jobs.Where(x=>x.PluginId==id).ToArray();
                        return new RunnerDescription(id,hash,start.Nonce,Environment.ProcessId,jobs.Select(x=>x.Descriptor).ToArray(),
                            jobs.FirstOrDefault()?.MaximumConcurrency??1,jobs.Where(x=>x.ExecutionGroup is not null).ToDictionary(x=>x.Descriptor.JobId,x=>x.ExecutionGroup!),catalog.SupportsQueries(id));
                    }
                    finally{if(start.DataKey is not null)CryptographicOperations.ZeroMemory(start.DataKey);}
                }
                if(catalog is null)throw new InvalidOperationException("Runner has not started.");
                if(message.Command=="Query")return await catalog.QueryAsync(id,RunnerChannel.Data<string>(message),token).ConfigureAwait(false);
                if(message.Command=="Execute")
                {
                    // 持久业务命令ID与临时RPC请求ID分别传递，重试与进度不能混用标识。
                    var request=RunnerChannel.Data<RunnerExecution>(message);
                    if(request.Payload?.Length>32768)throw new InvalidDataException();
                    var session=catalog.Jobs.Single(x=>x.PluginId==id&&x.Descriptor.JobId==request.JobId).Session;
                    if(session is IProgressBackgroundSession progress)return await progress.ExecuteWithProgressAsync(request.JobId,request.Payload,
                        async value=>{await channel!.CallAsync<bool>("Progress",new RunnerProgress(message.Id,value),token).ConfigureAwait(false);},token,request.CommandId).ConfigureAwait(false);
                    return session is IParameterizedBackgroundSession parameterized
                        ?await parameterized.ExecuteAsync(request.JobId,request.Payload,token,request.CommandId).ConfigureAwait(false)
                        :await session.ExecuteAsync(request.JobId,token).ConfigureAwait(false);
                }
                if(message.Command=="Stop")
                {await catalog.DisposeAsync().ConfigureAwait(false);catalog=null;cleanStop=true;return true;}
                throw new ArgumentException("Unknown Runner command.");
            });
            try{await channel.Completion.ConfigureAwait(false);}catch(IOException){}
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine("RUNNER_FAILED:"+error.GetType().Name);return 1;}
        finally
        {
            // 意外断连不能提前释放插件目录租约；强制进程退出后由OS关闭句柄，防止新Worker与旧线程重叠写库。
            if(started!=0&&!cleanStop)Environment.Exit(2);
            if(diagnostics is not null)await diagnostics.DisposeAsync().ConfigureAwait(false);
            if(channel is not null)await channel.DisposeAsync().ConfigureAwait(false);
            // 无法协作停止的插件由父进程Job Object关闭；不在EOF后无界等待。
            context?.Dispose();ownership?.Dispose();
        }
    }

    /// <summary>跨Worker重启也保持单写者；旧Runner被Job Object终止前，新Runner只能有界等待目录租约。</summary>
    private static async Task<FileStream> AcquireOwnershipAsync(string directory,CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        for(var current=new DirectoryInfo(directory);current is not null;current=current.Parent)
            if((current.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Runner data links are not allowed.");
        while(true)
        {
            token.ThrowIfCancellationRequested();
            try{return new FileStream(Path.Combine(directory,"runner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException error) when((error.HResult&0xffff) is 32 or 33){await Task.Delay(25,token).ConfigureAwait(false);}
        }
    }

    private sealed class RunnerContext(string id,string directory,byte[] sourceKey,RunnerChannel channel)
        : IBackgroundHostContext,IBackgroundSourceContext,IBackgroundNotificationContext,IDisposable
    {
        private readonly byte[] key=sourceKey.ToArray();
        public string PluginId=>id;
        public string DataDirectory=>directory;
        public byte[] OpenDataKey()=>key.ToArray();
        private T Sync<T>(string command,object data)
        {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));return channel.CallAsync<T>(command,data,timeout.Token).GetAwaiter().GetResult();}
        public void ImportSourceState(string host,SourceBudgetState state)=>Sync<bool>("SourceImport",new RunnerSource(host,state));
        public void ReportSourceFailure(string host,int status,DateTimeOffset? until=null)=>Sync<bool>("SourceFailure",new RunnerSource(host,Status:status,Until:until));
        public async Task<IBackgroundSourceLease> AcquireSourceAsync(string host,SourceBudgetState state,CancellationToken token)
            =>new SourceLease(await channel.CallAsync<Guid>("SourceAcquire",new RunnerSource(host,state),token).ConfigureAwait(false),this);
        public Task<IReadOnlyList<BackgroundNotificationChannel>> NotificationChannelsAsync(CancellationToken token)
            =>ChannelsAsync(token);
        private async Task<IReadOnlyList<BackgroundNotificationChannel>> ChannelsAsync(CancellationToken token)
            =>await channel.CallAsync<BackgroundNotificationChannel[]>("Channels",true,token).ConfigureAwait(false);
        public Task<BackgroundNotificationResult> SendNotificationAsync(string channelId,string profileId,BackgroundNotification message,CancellationToken token)
            =>channel.CallAsync<BackgroundNotificationResult>("Notify",new RunnerNotification(channelId,profileId,message),token);
        public void Dispose()=>CryptographicOperations.ZeroMemory(key);
        private sealed class SourceLease(Guid id,RunnerContext owner):IBackgroundSourceLease
        {
            // Complete记录最终HTTP分类，Dispose恰好回收一次宿主租约；子进程退出时宿主兜底回收。
            private int disposed,status;private DateTimeOffset? until;
            public void Complete(int value,DateTimeOffset? retryAfter=null){status=value;until=retryAfter;}
            public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)owner.Sync<bool>("SourceRelease",new RunnerSource("",Status:status,Until:until,Lease:id));}
        }
    }
}
