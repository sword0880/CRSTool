using System.IO;
using CRS.Worker.Abstractions;
using CRS.Worker.Service;

internal static class SourceBudgetPortChecks
{
    public static async Task RunAsync(Action<bool,string> check)
    {
        var state=new Dictionary<string,SourceBudgetState>();
        var store=new MemoryStore(state);
        DateTimeOffset blocked;
        using(var registry=new SourceBudgetRegistry(store))
        {
            check(registry.Snapshot().All(x=>x.State=="Ready"),"替换存储实现无需数据库即可读取健康");
            registry.ReportFailure("www.sse.com.cn",429,DateTimeOffset.UtcNow.AddDays(2));
            blocked=state["SSE"].BlockedUtc;
            check(blocked>DateTimeOffset.UtcNow.AddDays(1),"预算规则通过端口保存冷却");
            try { using var lease=await registry.AcquireAsync("plugin","query.sse.com.cn",Empty,default);throw new Exception("冷却被绕过"); }
            catch(SourceBudgetUnavailableException e){check(e.Code=="SourceCooldown","替换存储后仍跨域共享拒绝");}
            using var other=await registry.AcquireAsync("plugin","www.szse.cn",Empty,default);
            check(state["SZSE"].NextUtc>DateTimeOffset.UtcNow,"网络前通过端口提交预留");
        }
        check(store.Disposed,"注册表关闭释放注入的存储");
        using(var registry=new SourceBudgetRegistry(new MemoryStore(state)))
            check(registry.Snapshot().Single(x=>x.Source=="SSE").BlockedUtc==blocked,"替换存储重建注册表不改变已存冷却");
        // 基金官网与CDN同组，但不会继承交易所或发行人的拒绝状态。
        using(var registry=new SourceBudgetRegistry(new MemoryStore(state)))
        {
            registry.ReportFailure("www.efunds.com.cn",429,DateTimeOffset.UtcNow.AddHours(1));
            try { using var lease=await registry.AcquireAsync("nav","cdn.efunds.com.cn",Empty,default);throw new Exception("基金CDN绕过冷却"); }
            catch(SourceBudgetUnavailableException e){check(e.Code=="SourceCooldown","基金官网429阻断同组CDN");}
            check(registry.Snapshot().Single(x=>x.Source=="EFUNDS").BlockedUtc==state["EFUNDS"].BlockedUtc,"基金共享状态可观测且写入存储端口");
            using var independent=await registry.AcquireAsync("nav","www.chinacnd.com",Empty,default);
            check(registry.Snapshot().Single(x=>x.Source=="ISSUER-600153").Active==1,"基金冷却不阻断独立发行人");
        }
        var broken=new MemoryStore(state){FailRead=true};
        try{using var ignored=new SourceBudgetRegistry(broken);throw new Exception("读取失败被当空状态");}
        catch(IOException){check(broken.Disposed,"端口读取失败拒绝启动并释放资源");}
    }
    private static readonly SourceBudgetState Empty=new(DateTimeOffset.MinValue,DateTimeOffset.MinValue,0);
    // 仅验证策略与数据库适配器解耦，不提供持久性或生产部署能力。
    private sealed class MemoryStore(Dictionary<string,SourceBudgetState> states):ISourceBudgetStore
    {
        public bool Disposed,FailRead;
        public SourceBudgetState Read(string source)=>FailRead?throw new IOException("injected"):states.GetValueOrDefault(source,Empty);
        public void Save(string source,SourceBudgetState state)=>states[source]=state;
        public void Dispose()=>Disposed=true;
    }
}
