using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using CRS.Worker.Abstractions;

namespace CRS.Worker.PluginHost;

public sealed record RunnerMessage(int Version,Guid Id,string Kind,string Command,JsonElement? Data=null,string? Error=null);
/// <summary>启动材料仅经父子进程私有句柄发送，密钥不进入命令行、环境变量或临时文件。</summary>
public sealed record RunnerStart(string PluginId,string PackageHash,string Nonce,int ParentPid,string? DataDirectory,byte[]? DataKey);
public sealed record RunnerDescription(string PluginId,string PackageHash,string Nonce,int Pid,BackgroundJobDescriptor[] Jobs,
    int Concurrency,Dictionary<string,BackgroundExecutionGroup> Groups,bool SupportsQueries);
public sealed record RunnerExecution(string JobId,string? Payload,Guid? CommandId);
public sealed record RunnerProgress(Guid ExecutionId,BackgroundProgress Detail);
/// <summary>仅固定阶段与分类；宿主复核白名单后写生命周期诊断，不传异常正文。</summary>
public sealed record RunnerLifecycle(string Phase,string Code);
public sealed record RunnerSource(string Host,SourceBudgetState? State=null,int Status=0,DateTimeOffset? Until=null,Guid? Lease=null,string? FailureCode=null);
public sealed record RunnerNotification(string Channel,string Profile,BackgroundNotification Message);
/// <summary>仅私有句柄传递；维护上下文不进入普通RPC、命令行或临时文件。</summary>
public sealed record RunnerMaintenanceStart(string PackageHash,string Nonce,int ParentPid,BackgroundStorageMigrationContext Context);
public sealed record RunnerMaintenanceResult(string PluginId,string PackageHash,string Nonce,int Pid,BackgroundStorageMigrationResult Result);

/// <summary>私有继承stdio双向RPC；并行请求、取消和能力调用不经过桌面管道。</summary>
public sealed class RunnerChannel : IAsyncDisposable
{
    public const int Version=2;
    private const int MaximumFrame=131072;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=24};
    private sealed record Pending(TaskCompletionSource<RunnerMessage> Completion,Func<BackgroundProgress,Task>? Progress);
    private readonly Stream input,output;
    private readonly Func<RunnerMessage,CancellationToken,Task<object?>> handler;
    private readonly SemaphoreSlim writing=new(1);
    private readonly CancellationTokenSource stopping=new();
    private readonly ConcurrentDictionary<Guid,Pending> pending=[];
    private readonly ConcurrentDictionary<Guid,CancellationTokenSource> incoming=[];
    private readonly ConcurrentDictionary<Guid,Task> active=[];
    private readonly Task reading;
    public Task Completion=>reading;

    public RunnerChannel(Stream input,Stream output,Func<RunnerMessage,CancellationToken,Task<object?>> handler)
    {this.input=input;this.output=output;this.handler=handler;reading=ReadLoopAsync();}
    public static T Data<T>(RunnerMessage message)
    {
        if(message.Data is not {} data)throw new InvalidDataException("Runner payload missing.");
        var value=data.Deserialize<T>(Json);
        return value is null?throw new InvalidDataException("Runner payload missing."):value;
    }
    public static string PackageHash(TrustedPackage package)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new{package.Id,package.Version,Files=package.Files.OrderBy(x=>x.Key,StringComparer.Ordinal).ToArray()},Json)));

    public async Task<T> CallAsync<T>(string command,object? data,CancellationToken token,Func<BackgroundProgress,Task>? progress=null)
    {
        token.ThrowIfCancellationRequested();
        if(pending.Count>=32)throw new IOException("Runner request capacity exceeded.");
        var id=Guid.NewGuid();var item=new Pending(new(TaskCreationOptions.RunContinuationsAsynchronously),progress);
        if(!pending.TryAdd(id,item))throw new IOException("Runner request collision.");
        try
        {
            await SendAsync(new(Version,id,"Request",command,Element(data)),token).ConfigureAwait(false);
            RunnerMessage response;
            try{response=await item.Completion.Task.WaitAsync(token).ConfigureAwait(false);}
            catch(OperationCanceledException) when(token.IsCancellationRequested)
            {
                // 协作取消有独立收尾期限；超时只向进程所有者报告结果未知，不能无限占住宿主。
                using var grace=new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                try
                {
                    await SendAsync(new(Version,id,"Cancel",command),grace.Token).ConfigureAwait(false);
                    response=await item.Completion.Task.WaitAsync(grace.Token).ConfigureAwait(false);
                }
                catch(OperationCanceledException){throw new RunnerOutcomeUnknownException();}
            }
            if(response.Command!=command)throw new InvalidDataException("Runner response binding mismatch.");
            if(response.Error is {} error)
            {
                if(error==nameof(OperationCanceledException))throw new OperationCanceledException(token);
                if(error==nameof(SourceBudgetUnavailableException))
                {
                    var state=Data<RunnerSource>(response);var code=state.FailureCode??"SourceBudgetUnavailable";
                    if(!System.Text.RegularExpressions.Regex.IsMatch(code,"^[A-Za-z][A-Za-z0-9]{0,79}$"))throw new InvalidDataException("Invalid source failure classification.");
                    throw new SourceBudgetUnavailableException(code,state.Until);
                }
                throw new RunnerCallException(error);
            }
            return Data<T>(response);
        }
        finally{pending.TryRemove(id,out _);}
    }
    public Task ProgressAsync(RunnerProgress value)
    {
        if(!pending.TryGetValue(value.ExecutionId,out var operation)||operation.Progress is null)
            throw new InvalidDataException("Progress is not bound to an execution.");
        return operation.Progress(value.Detail);
    }
    private static JsonElement? Element(object? value)=>JsonSerializer.SerializeToElement(value,Json);
    private async Task SendAsync(RunnerMessage message,CancellationToken token)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(message,Json);
        if(bytes.Length is <2 or >MaximumFrame)throw new InvalidDataException("Runner frame limit exceeded.");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await writing.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header,token).ConfigureAwait(false);await output.WriteAsync(bytes,token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        finally{writing.Release();}
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while(true)
            {
                var header=new byte[4];await input.ReadExactlyAsync(header,stopping.Token).ConfigureAwait(false);
                var length=BinaryPrimitives.ReadInt32LittleEndian(header);
                if(length is <2 or >MaximumFrame)throw new InvalidDataException("Runner frame limit exceeded.");
                var bytes=new byte[length];await input.ReadExactlyAsync(bytes,stopping.Token).ConfigureAwait(false);
                var message=JsonSerializer.Deserialize<RunnerMessage>(bytes,Json)??throw new InvalidDataException("Runner frame missing.");
                if(message.Version!=Version||message.Id==Guid.Empty||message.Command.Length is <1 or >40)
                    throw new InvalidDataException("Runner frame version or identity rejected.");
                if(message.Kind=="Response")
                {
                    if(pending.TryGetValue(message.Id,out var operation))operation.Completion.TrySetResult(message);
                    continue;
                }
                if(message.Kind=="Cancel")
                // 取消回调也是插件代码，必须离开读帧循环执行，否则失控回调会阻断所有控制消息。
                {if(incoming.TryGetValue(message.Id,out var cancel))_ = Task.Run(()=>{try{cancel.Cancel();}catch(Exception){}});continue;}
                if(message.Kind!="Request"||incoming.Count>=32)throw new InvalidDataException("Runner request rejected.");
                var source=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                if(!incoming.TryAdd(message.Id,source)){source.Dispose();throw new InvalidDataException("Duplicate Runner request.");}
                var task=Task.Run(()=>HandleAsync(message,source));active[message.Id]=task;
                _=task.ContinueWith(completed=>{active.TryRemove(message.Id,out _);_ = completed.Exception;},TaskScheduler.Default);
            }
        }
        catch(Exception error)
        {
            foreach(var operation in pending.Values)operation.Completion.TrySetException(new RunnerOutcomeUnknownException());
            foreach(var source in incoming.Values)_=Task.Run(()=>{try{source.Cancel();}catch(Exception){}});
            if(!stopping.IsCancellationRequested)throw new IOException("Runner transport ended.",error);
        }
    }
    private async Task HandleAsync(RunnerMessage message,CancellationTokenSource source)
    {
        try
        {
            object? result=null;string? error=null;
            try{result=await handler(message,source.Token).ConfigureAwait(false);}
            // 来源冷却/容量分类和截止时间属于重试语义，不能跨进程后全部退化为同一种错误。
            catch(SourceBudgetUnavailableException failure){error=nameof(SourceBudgetUnavailableException);result=new RunnerSource("",Until:failure.Until,FailureCode:failure.Code);}
            // 仅跨进程传递固定错误分类，禁止透传第三方异常正文和堆栈。
            catch(Exception failure){error=failure switch
            {OperationCanceledException=>nameof(OperationCanceledException),ArgumentException=>nameof(ArgumentException),InvalidDataException=>nameof(InvalidDataException),_=>"OperationFailed"};}
            await SendAsync(new(Version,message.Id,"Response",message.Command,Element(result),error),stopping.Token).ConfigureAwait(false);
        }
        finally{incoming.TryRemove(message.Id,out _);source.Dispose();}
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();input.Dispose();output.Dispose();
        try{await reading.ConfigureAwait(false);}catch(Exception){}
        // 插件忽略取消时由进程所有者终止；这里绝不无界等待插件任务。
    }
}
public sealed class RunnerOutcomeUnknownException() : IOException("Runner stopped before its operation outcome was confirmed.");
public sealed class RunnerCallException(string code) : Exception("Runner operation failed.") {public string Code{get;}=code;}
