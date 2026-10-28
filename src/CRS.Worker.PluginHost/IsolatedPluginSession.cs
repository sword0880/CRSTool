using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using CRS.Worker.Abstractions;

namespace CRS.Worker.PluginHost;

/// <summary>每插件一个受监督Runner；只转发其专属密钥及窄宿主能力，不在Worker加载业务代码。</summary>
public sealed class IsolatedPluginSession(string runnerPath,string root,string trustFile,TrustedPackage package,
    IBackgroundHostContext? context=null,Action<string,string,string>? diagnostic=null,TimeProvider? timeProvider=null) : IBackgroundPluginSession,
    IProgressBackgroundSession,IParameterizedBackgroundSession,IBackgroundQuerySession,IGroupedBackgroundSession,IBackgroundQueryRoutingSession
{
    private readonly SemaphoreSlim lifecycle=new(1);
    private readonly ConcurrentDictionary<Guid,IBackgroundSourceLease> leases=[];
    private readonly object leaseGate=new();
    private long generation;
    private readonly Queue<long> restarts=[];
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private Process? process;
    private RunnerProcessJob? job;
    private RunnerChannel? channel;
    private RunnerDescription? description;
    private long restartAfter;
    private bool stopped,exhausted;
    public string? FailureCode{get;private set;}
    public bool CanRun=>!stopped&&!exhausted;
    public int MaximumConcurrentExecutions=>description?.Concurrency??1;
    public BackgroundExecutionGroup ExecutionGroup(string id)=>description?.Groups.GetValueOrDefault(id)
        ??new("default",MaximumConcurrentExecutions);
    public IReadOnlyList<BackgroundJobDescriptor> DescribeJobs()=>description?.Jobs??[];
    public BackgroundQueryRouting? DescribeQueryRouting()=>description?.QueryRouting;
    public async ValueTask StartAsync(CancellationToken token)=>await EnsureStartedAsync(token).ConfigureAwait(false);

    private async Task EnsureStartedAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(stopped||exhausted)throw new IOException("Runner is disabled.");
            if(process is {HasExited:false}&&channel is {Completion.IsCompleted:false})return;
            if(clock.GetTimestamp()<restartAfter)throw new IOException("Runner restart is cooling down.");
            await TerminateAsync().ConfigureAwait(false);
            var now=clock.GetTimestamp();
            // 十分钟最多启动三次，失败后至少等待十秒；不会以新命令ID重放未知结果。
            while(restarts.TryPeek(out var at)&&now-at>clock.TimestampFrequency*600L)restarts.Dequeue();
            if(restarts.Count>=3){exhausted=true;FailureCode="RunnerRestartBudgetExhausted";throw new IOException("Runner restart budget exhausted.");}
            restarts.Enqueue(now);
            var start=new ProcessStartInfo(Path.GetFullPath(runnerPath))
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(Path.GetFullPath(runnerPath))!};
            start.ArgumentList.Add("--supervised");start.ArgumentList.Add("--plugin-root");start.ArgumentList.Add(Path.GetFullPath(root));
            start.ArgumentList.Add("--trust-file");start.ArgumentList.Add(Path.GetFullPath(trustFile));
            start.ArgumentList.Add("--plugin-id");start.ArgumentList.Add(package.Id);
            try
            {
                process=Process.Start(start)??throw new IOException("Runner could not start.");
                job=new RunnerProcessJob(process);
                _=DrainErrorsAsync(process.StandardError); // 有界丢弃插件输出，不把敏感日志转发公开渠道。
                var epoch=Interlocked.Increment(ref generation);
                RunnerChannel? connection=null;
                connection=new RunnerChannel(process.StandardOutput.BaseStream,process.StandardInput.BaseStream,
                    (message,cancellation)=>HandleCapabilityAsync(connection!,epoch,message,cancellation));
                channel=connection;
                byte[]? key=null;
                try
                {
                    // 包摘要、随机挑战和实际子进程PID共同绑定本次启动，防止误接旧会话。
                    key=context?.OpenDataKey();var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    var ready=await channel.CallAsync<RunnerDescription>("Start",new RunnerStart(package.Id,RunnerChannel.PackageHash(package),nonce,
                        Environment.ProcessId,context?.DataDirectory,key),token).ConfigureAwait(false);
                    if(ready.PluginId!=package.Id||ready.PackageHash!=RunnerChannel.PackageHash(package)||ready.Nonce!=nonce||ready.Pid!=process.Id
                        ||ready.Concurrency is <1 or >32||ready.Jobs.Length>32)
                        throw new InvalidDataException("Runner handshake rejected.");
                    if(ready.QueryRouting is {} routing)ready=ready with{QueryRouting=routing.Validate()};
                    if(description is not null&&(System.Text.Json.JsonSerializer.Serialize(description.Jobs)!=System.Text.Json.JsonSerializer.Serialize(ready.Jobs)
                        ||description.Concurrency!=ready.Concurrency||System.Text.Json.JsonSerializer.Serialize(description.Groups)!=System.Text.Json.JsonSerializer.Serialize(ready.Groups)
                        ||System.Text.Json.JsonSerializer.Serialize(description.QueryRouting)!=System.Text.Json.JsonSerializer.Serialize(ready.QueryRouting)))
                        throw new InvalidDataException("Restart changed Runner scheduling contract.");
                    description=ready;FailureCode=null;
                    diagnostic?.Invoke(package.Id,"Runner","Started");
                }
                finally{if(key is not null)CryptographicOperations.ZeroMemory(key);}
            }
            catch(Exception error)
            {
                diagnostic?.Invoke(package.Id,"RunnerStart",error is System.ComponentModel.Win32Exception native
                    ?"Win32="+native.NativeErrorCode:error.GetType().Name);
                FailureCode="RunnerStartFailed";restartAfter=clock.GetTimestamp()+clock.TimestampFrequency*10L;
                await TerminateAsync().ConfigureAwait(false);throw;
            }
        }
        finally{lifecycle.Release();}
    }
    private async Task<T> InvokeAsync<T>(string command,object? data,CancellationToken token,Func<BackgroundProgress,Task>? progress=null)
    {
        await EnsureStartedAsync(token).ConfigureAwait(false);
        var current=channel!;
        try{return await current.CallAsync<T>(command,data,token,progress).ConfigureAwait(false);}
        catch(RunnerCallException error)
        {
            // 业务拒绝和证据损坏不等于传输故障，不能因普通查询失败终止正在采集的进程。
            if(error.Code==nameof(ArgumentException))throw new ArgumentException("Runner request rejected.");
            if(error.Code==nameof(InvalidDataException))throw new InvalidDataException("Runner data rejected.");
            if(error.Code==nameof(CRS.Common.Concurrency.WorkAdmissionException))throw new CRS.Common.Concurrency.WorkAdmissionException("Capacity");
            throw;
        }
        catch(Exception error) when(error is IOException or InvalidDataException)
        {
            await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if(ReferenceEquals(current,channel))
                {
                    FailureCode="RunnerOutcomeUnknown";restartAfter=clock.GetTimestamp()+clock.TimestampFrequency*10L;
                    await TerminateAsync().ConfigureAwait(false);diagnostic?.Invoke(package.Id,"Runner","OutcomeUnknown");
                }
            }
            finally{lifecycle.Release();}
            throw new RunnerOutcomeUnknownException();
        }
    }
    public Task<BackgroundJobResult> ExecuteAsync(string id,CancellationToken token)=>ExecuteAsync(id,null,token);
    public Task<BackgroundJobResult> ExecuteAsync(string id,string? payload,CancellationToken token,Guid? commandId=null)
        =>ExecuteWithProgressAsync(id,payload,_=>Task.CompletedTask,token,commandId);
    public Task<BackgroundJobResult> ExecuteWithProgressAsync(string id,string? payload,Func<BackgroundProgress,Task> progress,CancellationToken token,Guid? commandId=null)
        =>InvokeAsync<BackgroundJobResult>("Execute",new RunnerExecution(id,payload,commandId),token,progress);
    public Task<string> QueryAsync(string payload,CancellationToken token)=>description?.SupportsQueries==false
        ?throw new ArgumentException("Plugin has no queries."):InvokeAsync<string>("Query",payload,token);

    private async Task<object?> HandleCapabilityAsync(RunnerChannel connection,long epoch,RunnerMessage message,CancellationToken token)
    {
        // 此实例的context在包验证后由Worker绑定，子进程不能自报其它插件身份。
        if(epoch!=Interlocked.Read(ref generation))throw new OperationCanceledException(token);
        if(message.Command=="Progress")
        {await connection.ProgressAsync(RunnerChannel.Data<RunnerProgress>(message)).ConfigureAwait(false);return true;}
        if(message.Command=="Lifecycle")
        {
            var value=RunnerChannel.Data<RunnerLifecycle>(message);
            if(!System.Text.RegularExpressions.Regex.IsMatch(value.Phase,"^[A-Za-z][A-Za-z0-9.-]{0,60}$")
                ||!System.Text.RegularExpressions.Regex.IsMatch(value.Code,"^[A-Za-z][A-Za-z0-9.=]{0,80}$"))throw new InvalidDataException();
            diagnostic?.Invoke(package.Id,value.Phase,value.Code);return true;
        }
        if(message.Command=="Channels")return await (context as IBackgroundNotificationContext
            ??throw new InvalidOperationException()).NotificationChannelsAsync(token).ConfigureAwait(false);
        if(message.Command=="Notify")
        {
            var value=RunnerChannel.Data<RunnerNotification>(message);
            return await (context as IBackgroundNotificationContext??throw new InvalidOperationException())
                .SendNotificationAsync(value.Channel,value.Profile,value.Message,token).ConfigureAwait(false);
        }
        var source=context as IBackgroundSourceContext??throw new InvalidOperationException("Source capability unavailable.");
        var request=RunnerChannel.Data<RunnerSource>(message);
        if(message.Command=="SourceImport"){source.ImportSourceState(request.Host,request.State??throw new InvalidDataException());return true;}
        if(message.Command=="SourceFailure"){source.ReportSourceFailure(request.Host,request.Status,request.Until);return true;}
        if(message.Command=="SourceAcquire")
        {
            if(leases.Count>=32)throw new InvalidDataException("Runner source lease budget exceeded.");
            var lease=await source.AcquireSourceAsync(request.Host,request.State??throw new InvalidDataException(),token).ConfigureAwait(false);
            lock(leaseGate)
            {
                // 取消/退出可能与来源放行同时发生，迟到租约必须立刻归还，不能留到下次重启。
                if(token.IsCancellationRequested||epoch!=Interlocked.Read(ref generation))
                {lease.Dispose();throw new OperationCanceledException(token);}
                var id=Guid.NewGuid();if(!leases.TryAdd(id,lease)){lease.Dispose();throw new IOException();}return id;
            }
        }
        if(message.Command=="SourceRelease"&&request.Lease is {} leaseId&&leases.TryRemove(leaseId,out var held))
        {using(held){if(request.Status!=0)held.Complete(request.Status,request.Until);}return true;}
        throw new ArgumentException("Unsupported Runner capability.");
    }
    private static async Task DrainErrorsAsync(StreamReader reader)
    {try{var buffer=new char[1024];while(await reader.ReadAsync(buffer).ConfigureAwait(false)>0){}}catch(Exception){}}
    private async Task TerminateAsync()
    {
        // 先终止进程树并确认旧PID退出，再回收来源租约；绝不同时打开两个插件写库会话。
        Interlocked.Increment(ref generation);
        job?.Dispose();job=null;
        if(process is not null)
        {
            try
            {
                if(!process.HasExited)process.Kill(entireProcessTree:true);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch(InvalidOperationException){}
            catch(OperationCanceledException){exhausted=true;FailureCode="RunnerTerminationUnconfirmed";throw new IOException("Old Runner exit not confirmed.");}
            process.Dispose();process=null;
        }
        if(channel is not null){await channel.DisposeAsync().ConfigureAwait(false);channel=null;}
        lock(leaseGate)foreach(var id in leases.Keys)if(leases.TryRemove(id,out var lease))lease.Dispose();
    }
    public async ValueTask StopAsync(CancellationToken token)
    {
        await lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            stopped=true;
            if(channel is not null&&process is {HasExited:false})
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try{await channel.CallAsync<bool>("Stop",true,deadline.Token).ConfigureAwait(false);}catch(Exception){}
            }
            await TerminateAsync().ConfigureAwait(false);
        }
        finally{lifecycle.Release();}
    }
    public async ValueTask DisposeAsync()=>await StopAsync(CancellationToken.None).ConfigureAwait(false);
}
