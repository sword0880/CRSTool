using System.IO;
using System.Security.Cryptography;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class PluginStorageProviderChecks
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.PluginStorage",Guid.NewGuid().ToString("N"));
        var passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS 插件存储提供方："+name);}
        void Reject(Action work,string name)
        {try{work();throw new Exception("意外允许");}catch(Exception e) when(e is ArgumentException or NotSupportedException or InvalidDataException or System.Text.Json.JsonException){Check(true,name);}}
        var registry=new NationalTeamStorageProviders();
        Reject(()=>registry.Create("postgresql"),"未注册类型拒绝");
        Reject(()=>registry.Create("../sqlite"),"非法类型标识拒绝");
        Reject(()=>new NationalTeamStorageProviders([new("SQLITE",()=>registry.Create("sqlite"))]),"内置类型不能被覆盖");
        await using(var invalid=new NationalTeamBackgroundSession(new Context(root),databaseProvider:"missing"))
        {
            try{await invalid.StartAsync(default);throw new Exception("未知类型被允许");}
            catch(NotSupportedException){Check(!Directory.Exists(root),"未知类型在创建目录和获取密钥前拒绝");}
        }
        Directory.CreateDirectory(root);
        var config=Path.Combine(root,"storage.json");
        Check(NationalTeamStorageProviders.ConfiguredProvider(config)=="sqlite","国家队缺省使用普通SQLite");
        File.WriteAllText(config,"{\"Version\":1,\"Vault\":\"sqlcipher\",\"NationalTeam\":\"fixture\"}");
        Check(NationalTeamStorageProviders.ConfiguredProvider(config)=="fixture","统一配置独立选择插件数据库");
        File.WriteAllText(config,"{\"Version\":2,\"Vault\":\"sqlcipher\",\"NationalTeam\":\"fixture\"}");
        Reject(()=>NationalTeamStorageProviders.ConfiguredProvider(config),"未知配置版本拒绝");
        var recording=new RecordingProvider(registry.Create("sqlite"));var calls=0;
        var custom=new NationalTeamStorageProviders([new("fixture",()=>{calls++;return recording;})]);
        await using(var session=new NationalTeamBackgroundSession(new Context(Path.Combine(root,"custom")),databaseProvider:"fixture",storageProviders:custom))
        {
            await session.StartAsync(default);
            Check(calls==1&&recording.Markets==1&&recording.Policies==1,"选择一次存储族同时装配市场与策略仓储");
        }
        // 正式会话必须在开放作业前拒绝能力不全的提供方，并释放已经打开的连接。
        var incomplete=new IncompleteProvider(registry.Create("sqlite"));
        var incompleteRegistry=new NationalTeamStorageProviders([new("incomplete",()=>incomplete)]);
        await using(var session=new NationalTeamBackgroundSession(new Context(Path.Combine(root,"incomplete")),databaseProvider:"incomplete",storageProviders:incompleteRegistry))
        {
            try{await session.StartAsync(default);throw new Exception("能力缺失仍允许启动。");}
            catch(NotSupportedException){Check(incomplete.Proxy?.Disposed==true,"能力缺失在正式启动时拒绝并释放连接");}
        }
        // 故障转换在用例边界，取消仍必须保持取消语义。
        var key=RandomNumberGenerator.GetBytes(32);
        try
        {
            using var repository=registry.Create("sqlite").OpenMarket(Path.Combine(root,"failure"),new RawEvidenceStore(Path.Combine(root,"raw")),key);
            try{repository.ExecuteOperation<int>(()=>throw new Microsoft.Data.Sqlite.SqliteException("injected",1),default);throw new Exception("未转换故障");}
            catch(IOException e) when(e.InnerException is Microsoft.Data.Sqlite.SqliteException){Check(true,"市场仓储用例边界转换提供方故障");}
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try{repository.ExecuteOperation(()=>1,cancel.Token);throw new Exception("取消被忽略");}
            catch(OperationCanceledException){Check(true,"取消保持取消语义");}
        }
        finally{CryptographicOperations.ZeroMemory(key);}
        Console.WriteLine($"插件存储提供方通过：{passed}项。");
    }
    private sealed class Context(string path):IBackgroundHostContext
    {
        public string PluginId=>"crs.national-team";
        public string DataDirectory=>path;
        public byte[] OpenDataKey()=>RandomNumberGenerator.GetBytes(32);
    }
    // 仅转发现有SQLite族验证工厂接线，不是第二数据库实现。
    private sealed class RecordingProvider(INationalTeamStorageProvider inner):INationalTeamStorageProvider
    {
        public int Markets,Policies;
        public IMarketRepository OpenMarket(string directory,RawEvidenceStore raw,byte[] key)
        {Markets++;return inner.OpenMarket(directory,raw,key);}
        public ISourceRequestPolicy OpenSourcePolicy(string directory)
        {Policies++;return inner.OpenSourcePolicy(directory);}
    }
    private sealed class IncompleteProvider(INationalTeamStorageProvider inner):INationalTeamStorageProvider
    {
        public IncompleteRepositoryProxy? Proxy {get;private set;}
        public IMarketRepository OpenMarket(string directory,RawEvidenceStore raw,byte[] key)
        {
            var value=System.Reflection.DispatchProxy.Create<IMarketRepository,IncompleteRepositoryProxy>();
            Proxy=(IncompleteRepositoryProxy)value;Proxy.Inner=inner.OpenMarket(directory,raw,key);return value;
        }
        public ISourceRequestPolicy OpenSourcePolicy(string directory)=>inner.OpenSourcePolicy(directory);
    }
}

// 基本方法转发真实普通SQLite存储，故意不声明额外能力，用于正式装配负例。
public class IncompleteRepositoryProxy:System.Reflection.DispatchProxy
{
    public IMarketRepository Inner {get;set;}=null!;
    public bool Disposed {get;private set;}
    protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
    {
        if(method!.Name==nameof(IDisposable.Dispose))Disposed=true;
        try{return method.Invoke(Inner,args);}
        catch(System.Reflection.TargetInvocationException error) when(error.InnerException is {} cause)
        {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();throw;}
    }
}
