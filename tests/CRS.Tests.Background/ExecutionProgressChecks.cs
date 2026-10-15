using CRS.Worker.Abstractions;
using CRS.Worker.Service;

/// <summary>真实加密仓储验证阶段持久化、截断、重启、历史过滤及输入边界。</summary>
internal static class ExecutionProgressChecks
{
    public static void Run(string root,byte[] key)
    {
        var path=Path.Combine(root,"progress.db");string id;int passed=0;
        void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS 阶段日志："+label);}
        using(var repository=new EncryptedTaskRepository(path,key))
        {
            repository.RegisterDefault("crs.national-team",new("sync","同步","0 0 0 * * ?","China Standard Time",true));
            repository.RegisterDefault("crs.heartbeat",new("heartbeat","心跳","0 0 0 * * ?","China Standard Time",true));
            var plan=repository.Plans().Single(x=>x.PluginId=="crs.national-team");id=repository.Begin(plan,DateTimeOffset.UtcNow);
            repository.AppendProgress(id,new("DayStarted",new(2026,10,14),"SSE"));
            Check(repository.FindExecution(id) is{Result:"Running",Progress.Count:1},"运行期间即可读到已提交阶段");
            var rejected=false;try{repository.AppendProgress(id,new("secret=private-path"));}catch(ArgumentException){rejected=true;}
            Check(rejected && repository.FindExecution(id)!.Progress!.Count==1,"拒绝任意文本且不破坏既有记录");
            for(var i=1;i<600;i++)repository.AppendProgress(id,new("DayCommitted",Received:5,Duplicates:5));
            var stages=repository.FindExecution(id)!.Progress!;
            Check(stages.Count==128 && stages[0].Sequence==473 && stages[^1].Sequence==600,"有界读取最近128条并保留真实递增序号");
            Check(stages.Zip(stages.Skip(1)).All(x=>x.First.Utc<=x.Second.Utc),"记录时间由宿主持久生成");
            var completed=repository.Begin(plan,DateTimeOffset.UtcNow);repository.End(completed,plan,BackgroundJobResult.Success());
            var heartbeat=repository.Plans().Single(x=>x.PluginId=="crs.heartbeat");
            for(var i=0;i<501;i++){var beat=repository.Begin(heartbeat,DateTimeOffset.UtcNow);repository.End(beat,heartbeat,BackgroundJobResult.Success());}
            Check(repository.HistoryPage(null,10,"crs.national-team").Rows is{Count:2} rows && rows.Any(x=>x.ExecutionId==id),"ETF历史不被较新的心跳挤出");
            Check(repository.FindExecution(completed)?.Result=="Succeeded","超过500次心跳后，已完成ETF历史仍保留");
            // 混合任务和插件验证筛选先于分页，不会漏掉被其他任务挤到后面的记录。
            repository.RegisterDefault("crs.national-team",new("review","审核","0 0 0 * * ?","China Standard Time",true));
            var review=repository.Plans().Single(x=>x.JobId=="review");
            for(var i=0;i<12;i++){var run=repository.Begin(review,DateTimeOffset.UtcNow);repository.End(run,review,BackgroundJobResult.Success());}
            var first=repository.HistoryPage(null,10,"crs.national-team","review");
            var second=repository.HistoryPage(first.NextCursor,10,"crs.national-team","review");
            Check(first.Rows.Count==10 && second.Rows.Count==2 && second.NextCursor is null
                && !first.Rows.Select(x=>x.ExecutionId).Intersect(second.Rows.Select(x=>x.ExecutionId)).Any(),"插件和任务组合筛选稳定分页且不重复");
            Check(repository.HistoryPage(null,10,"crs.national-team","sync").Rows.Count==2,"同插件的其他任务不会污染筛选");
            Check(repository.HistoryPage(null,10,null,"review").Rows.Count==10
                && repository.HistoryPage(null,10,"crs.heartbeat","review").Rows.Count==0,"支持单独任务筛选及无匹配组合");
        }
        using(var repository=new EncryptedTaskRepository(path,key))
        {
            var record=repository.FindExecution(id)!;
            Check(record.Result=="Interrupted" && record.Progress![^1].Sequence==600,"重启保留阶段并标记中断，不能伪造完成");
            repository.AppendProgress(id,new("RangeCompleted"));
            Check(repository.FindExecution(id)!.Progress![^1].Sequence==600,"完成或中断的执行拒绝后续阶段归入");
        }
        Console.WriteLine($"阶段日志仓储通过：{passed}项。");
    }
}
