using System.IO;
using System.Security.Cryptography;
using CRS.Plugins.NationalTeam.Jobs;
using Microsoft.Data.Sqlite;

internal static class LegacySourcePolicyRecoveryChecks
{
    public static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.LegacyPolicyRecovery",Guid.NewGuid().ToString("N"));var key=RandomNumberGenerator.GetBytes(32);
        const string host="www.sse.com.cn";
        void Check(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS 插件来源故障恢复："+label);}
        SqliteConnection Open(string folder)
        {var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(folder,"source-policy.db"),Password=Convert.ToHexString(key),Pooling=false}.ToString());c.Open();return c;}
        void Execute(SqliteConnection c,string sql){using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
        void Reject(Action action,string label)
        {try{action();}catch(Exception error) when(error is IOException or InvalidDataException or FormatException){Check(true,label);return;}throw new Exception(label);}
        var missing=Path.Combine(root,"missing");using(var policy=new SourceRequestPolicy(missing,key))policy.Failure(host,DateTimeOffset.UtcNow,403);
        using(var c=Open(missing))Execute(c,"DROP TABLE source_policy");
        Reject(()=>{using var p=new SourceRequestPolicy(missing,key);},"已初始化的缺表库拒绝启动");
        using(var c=Open(missing))
        {using var cmd=c.CreateCommand();cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE name='source_policy'";Check(Convert.ToInt32(cmd.ExecuteScalar())==0,"拒绝后没有静默重建状态表");}
        foreach(var change in new[]{"failures=-1","failures=4294967296","failures='bad'","next_utc='bad'","blocked_utc='bad'"})
        {
            var folder=Path.Combine(root,Guid.NewGuid().ToString("N"));using(var p=new SourceRequestPolicy(folder,key))p.Reserve(host,DateTimeOffset.UtcNow);
            using(var c=Open(folder))Execute(c,"UPDATE source_policy SET "+change);
            Reject(()=>{using var p=new SourceRequestPolicy(folder,key);},"启动拒绝损坏状态："+change);
        }
        var runtime=Path.Combine(root,"runtime");DateTimeOffset blocked;
        using(var policy=new SourceRequestPolicy(runtime,key))
        using(var c=Open(runtime))
        {
            policy.Failure(host,DateTimeOffset.UtcNow,403);blocked=policy.Read(host).Blocked;
            Execute(c,"CREATE TRIGGER fail_update BEFORE UPDATE ON source_policy BEGIN SELECT RAISE(ABORT,'synthetic'); END");
            Reject(()=>policy.Reserve(host,DateTimeOffset.UtcNow),"运行中写失败被抽象为IO错误");
            Execute(c,"DROP TRIGGER fail_update");
            Reject(()=>policy.Read("www.szse.cn"),"底层修复后本会话仍锁定全部来源");
        }
        using(var reopened=new SourceRequestPolicy(runtime,key))Check(reopened.Read(host).Blocked==blocked,"重新核验后保留原已提交冷却");
        using(var policy=new SourceRequestPolicy(runtime,key))
        using(var c=Open(runtime))
        {
            Execute(c,"UPDATE source_policy SET failures=-1");Reject(()=>policy.Read(host),"运行时读损坏立即拒绝");
            Execute(c,"UPDATE source_policy SET failures=1");Reject(()=>policy.Read(host),"运行时读故障修复后也不自动解除会话锁定");
        }
        using(var reopened=new SourceRequestPolicy(runtime,key))Check(reopened.Read(host).Blocked==blocked,"读故障后重建策略才恢复且冷却未缩短");
        CryptographicOperations.ZeroMemory(key);
    }
}
