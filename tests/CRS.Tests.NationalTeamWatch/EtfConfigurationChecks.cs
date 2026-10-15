using System.Text;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>配置测试只写隔离加密库，验证候选、编辑、并发修订、停用和重启，不访问官网。</summary>
internal static class EtfConfigurationChecks
{
    public static void Run(string root)
    {
        var directory=Path.Combine(root,"configuration");var raw=new RawEvidenceStore(Path.Combine(directory,"raw"));
        void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS ETF配置："+name);}
        void Reject(Action work,string name){try{work();}catch(Exception){Console.WriteLine("PASS ETF配置："+name);return;}throw new Exception(name);}
        using(var repository=TestMarketData.Open(Path.Combine(directory,"data"),raw))
        {
            var configurations=repository.Configuration();
            Check(configurations.Count==24 && configurations.Count(x=>x.Active)==5,"23个参考代码与初始池合并为24个候选，仅原5只监控");
            var key=new EtfKey("SSE","510500");var candidate=configurations.Single(x=>x.Ticker==key.Ticker);
            repository.SaveConfiguration(key,"合成修改名称","合成分组",false,"合成配置编辑",candidate.Revision);
            var edited=repository.Configuration().Single(x=>x.Ticker==key.Ticker);
            Check(edited.Name=="合成修改名称" && edited.IndexGroup=="合成分组" && edited.Revision==1 && !edited.Active,"已有ETF名称与指数可修改，修订递增");
            Reject(()=>repository.SaveConfiguration(key,"覆盖旧版本","旧分组",true,"并发旧窗口",candidate.Revision),"旧窗口修订被拒绝");
            Check(repository.Configuration().Single(x=>x.Ticker==key.Ticker)==edited,"拒绝并发后保留已提交配置");
            repository.SaveConfiguration(key,edited.Name,edited.IndexGroup,true,"启用合成候选",edited.Revision);
            Check(repository.Overview().Any(x=>x.Ticker==key.Ticker && x.Label=="DataPilot"),"启用候选进入概览但不伪造机构持有标签");
            var existing=new EtfKey("SSE","510300");var item=repository.Configuration().Single(x=>x.Ticker==existing.Ticker);
            var hash=raw.Save(Encoding.UTF8.GetBytes("synthetic configuration evidence"));
            var fetch=repository.RecordFetch(hash,"https://www.sse.com.cn/synthetic-configuration-evidence");
            repository.ImportCalendar(fetch,[("SSE",new(2026,9,1))],default);
            var added=repository.ImportSharesDetailed(fetch,[new(existing,new(2026,9,1),100,"份")],default);
            var repeat=repository.ImportSharesDetailed(repository.RecordFetch(hash,"https://www.sse.com.cn/synthetic-repeat"),[new(existing,new(2026,9,1),100,"份")],default);
            var changed=repository.ImportSharesDetailed(repository.RecordFetch(hash,"https://www.sse.com.cn/synthetic-revision"),[new(existing,new(2026,9,1),101,"份")],default);
            Check(added is{Added:1,Duplicates:0,Pending:0} && repeat is{Added:0,Duplicates:1,Pending:0} && changed is{Added:0,Duplicates:0,Pending:1},"采集数量明确区分首次接受、重复跳过和待审核修订");
            repository.SaveConfiguration(existing,item.Name,item.IndexGroup,false,"停用并保留历史",item.Revision);
            Check(repository.Evidence().Count==2 && !repository.Overview().Any(x=>x.Ticker==existing.Ticker) && repository.Configuration().Single(x=>x.Ticker==existing.Ticker).Active==false,"停用只退出采集池，目录和已接受/待审核事实保留");
            var custom=new EtfKey("SSE","510901");
            repository.SaveConfiguration(custom,"自定义合成ETF","合成分组",false,"新增合成候选",-1);
            Reject(()=>repository.SaveConfiguration(custom,"重复覆盖","合成分组",false,"重复新增",-1),"新增模式拒绝覆盖已有代码");
            Reject(()=>repository.SaveConfiguration(new("BAD","123456"),"无效标的","合成分组",false,"格式测试",-1),"非法市场拒绝");
            Reject(()=>repository.SaveConfiguration(custom,new string('名',101),"合成分组",false,"超长测试",1),"超长名称拒绝");
            var current=repository.Configuration().Single(x=>x.Ticker==key.Ticker);var command=Guid.NewGuid();
            repository.SaveConfiguration(key,current.Name,current.IndexGroup,false,"稳定命令停用",current.Revision,command);
            repository.SaveConfiguration(key,current.Name,current.IndexGroup,false,"稳定命令停用",current.Revision,command);
            Check(repository.Configuration().Single(x=>x.Ticker==key.Ticker).Revision==current.Revision+1,"业务提交后同一命令重放不再修改修订");
            var needed=20-repository.Configuration().Count(x=>x.Active);
            for(var index=0;index<needed;index++)repository.SaveConfiguration(new("SSE",(590000+index).ToString()),"合成容量ETF","合成分组",true,"容量检查",-1);
            var overflow=repository.Configuration().Single(x=>x.Ticker=="510310");
            Reject(()=>repository.SaveConfiguration(new(overflow.Market,overflow.Ticker),"超限不能覆盖名称",overflow.IndexGroup,true,"超限回滚",overflow.Revision),"第21只监控被数据库拒绝");
            Check(repository.Configuration().Single(x=>x.Ticker==overflow.Ticker)==overflow,"监控超限事务也回滚名称和修订修改");
            var active=repository.Configuration().First(x=>x.Active);
            repository.SaveConfiguration(new(active.Market,active.Ticker),"满池仍可编辑名称",active.IndexGroup,true,"普通名称编辑",active.Revision);
            Check(repository.Configuration().Single(x=>x.Market==active.Market && x.Ticker==active.Ticker).Name=="满池仍可编辑名称","满池时允许修改已启用ETF的名称和备注");
        }
        using(var reopened=TestMarketData.Open(Path.Combine(directory,"data"),raw))
        {
            Check(reopened.Configuration().Count(x=>x.Active)==20,"满20只监控时加密库仍可重启打开");
            Check(reopened.Configuration().Single(x=>x.Ticker=="510500") is{Name:"合成修改名称",Active:false},"重启不重置名称、分组或停用状态");
        }
    }
}
