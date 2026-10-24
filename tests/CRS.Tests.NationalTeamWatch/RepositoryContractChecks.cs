using System.Reflection;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>契约缺失在启动暴露；测试不以空集合模拟不存在的存储能力。</summary>
internal static class RepositoryContractChecks
{
    internal static void Run(string root)
    {
        var contracts=typeof(IMarketRepository).GetInterfaces().Append(typeof(IMarketRepository)).ToArray();
        var methods=contracts.SelectMany(x=>x.GetMethods()).ToArray();
        Check(methods.All(x=>x.IsAbstract),"所有仓储端口均要求显式实现，没有默认空集合或延迟抛错");
        Check(typeof(IEquityReadRepository).GetMethods().All(x=>!x.Name.StartsWith("Save")&&!x.Name.StartsWith("Review")),"披露读取端口不暴露审核写入");
        var key=System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var repository=new MarketRepository(root,new RawEvidenceStore(Path.Combine(root,"raw")),encryptionKey:key);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        MarketRepositoryCapabilities.Validate(repository,default);
        Check(true,"正式存储可打开独立读取并具有全部必需能力");
        // 只转发完整基本契约，故意不声明独立读取等附加能力。
        var incomplete=DispatchProxy.Create<IMarketRepository,ForwardingRepositoryProxy>();
        ((ForwardingRepositoryProxy)incomplete).Inner=repository;
        try { MarketRepositoryCapabilities.Validate(incomplete,default);throw new Exception("能力不足仍允许启动。"); }
        catch(NotSupportedException){Check(true,"能力缺失在启动核验阶段拒绝");}
        try { MarketRepositoryCapabilities.Validate(repository,new CancellationToken(true));throw new Exception("启动忽略取消。"); }
        catch(OperationCanceledException){Check(true,"启动能力核验保持取消语义");}
    }

    private static void Check(bool condition,string message)
    {if(!condition)throw new Exception(message);Console.WriteLine("PASS 仓储契约："+message);}
}

// 转发真实实现供能力负例使用，避免编写大量与测试无关的空方法。
public class ForwardingRepositoryProxy:DispatchProxy
{
    public IMarketRepository Inner {get;set;}=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        try{return method!.Invoke(Inner,args);}
        catch(TargetInvocationException error) when(error.InnerException is {} cause)
        {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();throw;}
    }
}
