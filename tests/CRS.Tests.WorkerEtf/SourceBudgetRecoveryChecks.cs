using System.IO;
using System.Security.Cryptography;
using System.Text;
using CRS.Worker.Abstractions;
using CRS.Worker.Ipc;
using CRS.Worker.PluginHost;
using CRS.Worker.Service;
using Microsoft.Data.Sqlite;

internal static class SourceBudgetRecoveryChecks
{
    public static async Task RunAsync(string root,byte[] key,Action<bool,string> check)
    {
        var empty=new SourceBudgetState(DateTimeOffset.MinValue,DateTimeOffset.MinValue,0);
        DateTimeOffset blocked;
        using(var sources=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key)))
        using(var repository=new EncryptedTaskRepository(Path.Combine(root,"scheduler.db"),key))
        await using(var catalog=new BackgroundPluginCatalog())
        await using(var runtime=new BackgroundRuntime(repository,catalog,sources:sources))
        using(var db=OpenBudget(root,key))
        {
            sources.ReportFailure("www.sse.com.cn",429,DateTimeOffset.UtcNow.AddDays(2));
            blocked=sources.Snapshot().Single(x=>x.Source=="SSE").BlockedUtc;
            using var command=db.CreateCommand();
            command.CommandText="CREATE TRIGGER fail_write BEFORE UPDATE ON source_budget BEGIN SELECT RAISE(ABORT,'injected'); END";
            command.ExecuteNonQuery();
            try{sources.ReportFailure("www.sse.com.cn",503);throw new Exception("写故障未上报。");}
            catch(IOException error) when(error.InnerException is SqliteException){check(true,"已存冷却后的写入故障以存储端口异常上报");}
            command.CommandText="DROP TRIGGER fail_write";command.ExecuteNonQuery();
            for(var i=0;i<2;i++)
            {
                var health=await runtime.HandleAsync(Request("GetHealth"),new("test",false),default);
                check(health is {Code:"Ok",Health.State:"StorageFault",Health.Sources:null},"来源故障后健康仍可读，来源快照明确不可用");
            }
            check((await runtime.HandleAsync(Request("RunNow"),new("test",true),default)).Code=="StorageFault","来源故障后宿主拒绝新增执行");
            check((await runtime.HandleAsync(Request("QueryPlugin"),new("test",false),default)).Code=="StorageFault","来源故障后宿主拒绝插件查询");
            try{using var ignored=await sources.AcquireAsync("new-plugin","www.szse.cn",empty,default);throw new Exception("存储故障被清除。");}
            catch(InvalidDataException){check(true,"修复注入条件后仍拒绝其他来源请求");}
        }
        using(var sources=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key)))
        {
            check(sources.Snapshot().Single(x=>x.Source=="SSE").BlockedUtc==blocked,"重启保留写故障前已提交的冷却期限");
            try{using var ignored=await sources.AcquireAsync("new-plugin","static.sse.com.cn",empty,default);throw new Exception("重启绕过冷却。");}
            catch(SourceBudgetUnavailableException error){check(error.Code=="SourceCooldown","新插件重启后仍不能绕过持久冷却");}
        }
        InvalidState(Path.Combine(root,"invalid-counter"),key,"failures=-1",check);
        InvalidState(Path.Combine(root,"overflow-counter"),key,"failures=9223372036854775807",check);
        InvalidState(Path.Combine(root,"invalid-date"),key,"next_utc='invalid'",check);
        InvalidState(Path.Combine(root,"future-version"),key,"",check);
        InvalidState(Path.Combine(root,"missing-table"),key,"DROP TABLE source_budget",check);
    }
    private static BackgroundRequest Request(string command)=>new(WorkerProtocol.Version,Guid.NewGuid(),command,"test","test");
    private static SqliteConnection OpenBudget(string root,byte[] key)
    {
        var derived=HKDF.DeriveKey(HashAlgorithmName.SHA256,key,32,info:Encoding.UTF8.GetBytes("CRS.Worker.SourceBudget/v1"));
        try
        {
            var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"source-budgets.db"),Password=Convert.ToHexString(derived),Pooling=false}.ToString());
            try{db.Open();return db;}catch{db.Dispose();throw;}
        }
        finally{CryptographicOperations.ZeroMemory(derived);}
    }
    private static void InvalidState(string root,byte[] key,string update,Action<bool,string> check)
    {
        using(var sources=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key)))sources.ReportFailure("www.sse.com.cn",403);
        using(var db=OpenBudget(root,key))
        {
            using var command=db.CreateCommand();
            command.CommandText=update.Length==0?"PRAGMA user_version=99":update=="DROP TABLE source_budget"?update:"UPDATE source_budget SET "+update;
            command.ExecuteNonQuery();
        }
        try{using var ignored=new SourceBudgetRegistry(new SqliteSourceBudgetStore(root,key));throw new Exception("无效预算意外获准启动。");}
        catch(Exception error) when(error is InvalidDataException or FormatException)
        {check(true,"损坏计数/日期或未知版本在启动前被拒绝："+Path.GetFileName(root));}
    }
}
