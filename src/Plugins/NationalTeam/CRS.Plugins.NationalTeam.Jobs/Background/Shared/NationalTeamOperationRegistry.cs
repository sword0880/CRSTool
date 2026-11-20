using System.Reflection;
using System.Text;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Worker.Abstractions;
namespace CRS.Plugins.NationalTeam.Jobs;

internal enum NationalTeamQueryKind { ShortRead, ResearchRead }
[AttributeUsage(AttributeTargets.Method,AllowMultiple=true)]
internal sealed class NationalTeamQueryAttribute(string operation,NationalTeamQueryKind kind=NationalTeamQueryKind.ResearchRead):Attribute
{
    public string Operation{get;}=operation;
    public NationalTeamQueryKind Kind{get;}=kind;
}
[AttributeUsage(AttributeTargets.Method)]
internal sealed class NationalTeamJobAttribute(string id,string name,string cron,string zone,bool enabled,bool startup=false,bool manual=false):Attribute
{
    public BackgroundJobDescriptor Descriptor{get;}=new(id,name,cron,zone,enabled,startup,manual);
    public string Group{get;init;}="management";
    public int MaximumConcurrency{get;init;}=1;
    public int Order{get;init;}
}
internal sealed record NationalTeamQueryResult(string[] Rows,string? Metadata=null);
internal sealed record NationalTeamQueryHandler(NationalTeamQueryKind Kind,Func<EtfRequest,CancellationToken,Task<NationalTeamQueryResult>> Read);
internal sealed record NationalTeamJobHandler(NationalTeamJobAttribute Registration,Func<EtfRequest,CancellationToken,Guid?,Task<BackgroundJobResult>> Execute);

/// <summary>仅扫描已核验入口类型自身的方法；功能在所属目录声明处理器，重复标识或错误签名使启动失败。</summary>
internal sealed class NationalTeamOperationRegistry
{
    public IReadOnlyDictionary<string,NationalTeamQueryHandler> Queries{get;}
    public IReadOnlyDictionary<string,NationalTeamJobHandler> Jobs{get;}
    public NationalTeamOperationRegistry(object session)
    {
        var queries=new Dictionary<string,NationalTeamQueryHandler>(StringComparer.Ordinal);
        var jobs=new Dictionary<string,NationalTeamJobHandler>(StringComparer.Ordinal);
        foreach(var method in session.GetType().GetMethods(BindingFlags.Instance|BindingFlags.NonPublic))
        {
            foreach(var declaration in method.GetCustomAttributes<NationalTeamQueryAttribute>())
            {
                ValidateId(declaration.Operation);
                if(!Enum.IsDefined(declaration.Kind))throw new InvalidDataException("查询类别声明无效。");
                var read=method.CreateDelegate<Func<EtfRequest,CancellationToken,Task<NationalTeamQueryResult>>>(session);
                if(!queries.TryAdd(declaration.Operation,new(declaration.Kind,read)))throw new InvalidDataException("重复的公开查询声明。");
            }
            if(method.GetCustomAttribute<NationalTeamJobAttribute>() is {} job)
            {
                ValidateId(job.Descriptor.JobId);ValidateId(job.Group);
                if(job.MaximumConcurrency is <1 or >4)throw new InvalidDataException("执行组并发声明无效。");
                var execute=method.CreateDelegate<Func<EtfRequest,CancellationToken,Guid?,Task<BackgroundJobResult>>>(session);
                if(!jobs.TryAdd(job.Descriptor.JobId,new(job,execute)))throw new InvalidDataException("重复的后台任务声明。");
            }
        }
        Queries=queries;Jobs=jobs;
    }
    private static void ValidateId(string id)
    {
        if(id.Length is <1 or >40||id[0] is <'a' or >'z'||id.Any(c=>c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw new InvalidDataException("操作标识不符合受限格式。");
    }
}

/// <summary>每个查询独有的有界序列化计数，不跨请求共享可变字段。</summary>
internal sealed class NationalTeamQuerySerialization(CancellationToken token)
{
    private int rows,bytes;
    public string Row<T>(T value)
    {
        token.ThrowIfCancellationRequested();var result=JsonSerializer.Serialize(value);bytes+=Encoding.UTF8.GetByteCount(result);
        if(++rows>10000||bytes>2*1024*1024)throw new ArgumentException("查询结果超过限额，请缩小查询范围。");
        return result;
    }
}
