using System.IO;
using System.Security.Cryptography;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class InitialEtfDirectoryChecks
{
    internal static void Run()
    {
        var root=Path.Combine(Path.GetTempPath(),"CRS.InitialEtfDirectory",Guid.NewGuid().ToString("N"));
        var data=Path.Combine(root,"data");var raw=new RawEvidenceStore(Path.Combine(root,"raw"));
        var key=RandomNumberGenerator.GetBytes(32);
        MarketRepository Open()=>new(data,raw,approvalAuditKey:key);
        var expected=InitialEtfDirectory.Entries.Select(x=>(x.Market,x.Ticker)).ToHashSet();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException(name);Console.WriteLine("PASS ETF初始化目录："+name);}
        try
        {
            Check(expected.Count==24&&InitialEtfDirectory.Entries.Count==24,"24只目录使用唯一市场和代码");
            using(var repository=Open())
            {
                var entries=repository.Configuration();
                Check(entries.Select(x=>(x.Market,x.Ticker)).ToHashSet().SetEquals(expected)&&entries.All(x=>x.Active&&x.Revision==0&&x.Origin==InitialEtfDirectory.Origin),"空加密库自动写入全部默认配置");
                var overview=repository.Overview();
                Check(overview.Count==24&&overview.All(x=>x.AsOf is null&&x.Shares is null&&x.Change1 is null&&x.Change5 is null&&x.Change20 is null),"没有采集事实仍有目录，金额及变化保持未知");
                var first=entries.First();
                repository.SaveConfiguration(new(first.Market,first.Ticker),"合成自定义名称","CUSTOM",false,"用户停用",first.Revision);
                repository.SaveConfiguration(new("SSE","599999"),"合成新增目录","CUSTOM",true,"用户新增",-1);
            }
            using(var repository=Open())
            {
                var entries=repository.Configuration();var changed=entries.Single(x=>x.Name=="合成自定义名称");
                Check(entries.Count==25&&!changed.Active&&changed.IndexGroup=="CUSTOM"&&changed.Revision==1&&changed.Reason=="用户停用","重启幂等且保留用户修改及停用");
                Check(entries.Any(x=>x.Ticker=="599999"&&x.Revision==1),"保留用户新增目录");
            }
            // 仅移走本测试自有的单个库文件，模拟原库已清空；保留原库便于失败诊断。
            File.Move(Path.Combine(data,"market.db"),Path.Combine(data,"market.db.previous"));
            using(var repository=Open())
            {
                var entries=repository.Configuration();
                Check(entries.Count==24&&entries.Select(x=>(x.Market,x.Ticker)).ToHashSet().SetEquals(expected),"原库移除后在相同目录自动重建内置清单");
                Check(repository.Evidence().Count==0,"初始化只生成目录，不伪造采集证据");
            }
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
}
