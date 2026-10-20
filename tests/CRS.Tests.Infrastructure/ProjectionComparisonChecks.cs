using System.Text.Json;
using CRS.Application.Performance;
using CRS.Domain;
using CRS.Domain.Performance;

internal static class ProjectionComparisonChecks
{
    public static async Task RunAsync(IPerformanceUseCases cases,PerformanceRun run,PerformanceRun replay)
    {
        void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 冻结投影比对："+text);}
        void Reject(Action action,string text){try{action();}catch(CrsException){Check(true,text);return;}throw new Exception(text);}
        PerformanceRun Freeze(PerformanceData data)=>PerformanceEvidence.Seal(run with{Id=Guid.NewGuid().ToString("N"),Input=data,InputHash=PerformanceSnapshot.Identity(data)});
        var count=(await cases.LoadAsync()).History.Length;
        var compared=await cases.CompareProjectionsAsync(run.Id,replay.Id);
        Check(compared.Comparable&&compared.SameFacts&&compared.VersionsFullyRecorded,"实际加密历史通过应用端口只读比对");
        Check((await cases.LoadAsync()).History.Length==count&&JsonSerializer.Serialize(await cases.OpenRunAsync(run.Id))==JsonSerializer.Serialize(run),"比对不生成历史或覆盖原记录");
        var versioned=PerformanceEvidence.Seal(run with{ImportProvenance=[..run.ImportProvenance.Select(x=>x with{ProjectionVersion="IBKR.Performance.future-test"})]});
        var versionResult=ProjectionComparer.Compare(run,versioned);
        Check(versionResult.SameFacts&&versionResult.LeftVersions[0].ProjectionVersion!=versionResult.RightVersions[0].ProjectionVersion,"版本差异与事实差异独立呈现");
        var events=run.Input.Events.ToArray();events[0]=events[0] with{Amount=events[0].Amount+1};
        var changed=ProjectionComparer.Compare(run,Freeze(run.Input with{Events=events}));
        Check(!changed.SameFacts&&changed.Changes.Single().ChangedFields.Contains("Amount")&&changed.Changes.Single().BeforeHash!=changed.Changes.Single().AfterHash,"定位金额字段及前后摘要，不输出正文原值");
        var added=ProjectionComparer.Compare(run,Freeze(run.Input with{Events=[..run.Input.Events,run.Input.Events[0] with{Id="synthetic.added"}]}));
        Check(added.Changes.Single().Change=="Added","新增事件有独立标识");
        var removed=ProjectionComparer.Compare(run,Freeze(run.Input with{Events=run.Input.Events.Skip(1).ToArray()}));
        Check(removed.Changes.Single().Change=="Removed","缺失事件标记删除");
        var ordered=ProjectionComparer.Compare(run,Freeze(run.Input with{Events=run.Input.Events.Reverse().ToArray()}));
        Check(ordered.SameFacts,"按事件标识比较，不因行顺序改变制造差异");
        var legacy=ProjectionComparer.Compare(run,PerformanceEvidence.Seal(run with{ImportProvenance=[]}));
        Check(legacy.Comparable&&legacy.SameFacts&&!legacy.VersionsFullyRecorded,"旧历史版本缺失保持未知");
        var other=PerformanceEvidence.Seal(run with{ImportProvenance=[..run.ImportProvenance.Select(x=>x with{SourceFileHash=new string('a',64),ImportBatchId=new string('a',64)})]});
        var unrelated=ProjectionComparer.Compare(run,other);
        Check(!unrelated.Comparable&&!unrelated.SameFacts&&unrelated.Changes.IsEmpty,"不同原件集合不强行对应");
        Reject(()=>ProjectionComparer.Compare(run,run with{InputHash=new string('0',64)}),"输入或结果摘要篡改拒绝");
        Reject(()=>ProjectionComparer.Compare(run,Freeze(run.Input with{Events=[..run.Input.Events,run.Input.Events[0]]})),"重复身份拒绝猜测对应关系");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await cases.CompareProjectionsAsync(run.Id,replay.Id,cancel.Token);throw new Exception("取消未生效");}catch(OperationCanceledException){Check(true,"取消应用请求不进入比对");}
    }
}
