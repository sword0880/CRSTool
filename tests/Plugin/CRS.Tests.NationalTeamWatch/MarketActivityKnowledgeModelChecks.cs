using System.IO;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class MarketActivityKnowledgeModelChecks
{
    internal static async Task RunAsync(MarketActivityKnowledgeReport report)
    {
        var workspace=new Workspace(report);var model=new MarketActivityKnowledgeViewModel(workspace,default){From=report.From.ToDateTime(TimeOnly.MinValue),Through=report.Through.ToDateTime(TimeOnly.MinValue)};
        await model.ReadAsync();Check(model.Rows.Count==report.Revisions.Count&&model.Status.Contains("无记录不代表零成交")&&workspace.Cutoff is null,"日期主动查询、截止留空由后台固定，空记录不解释为零成交");
        model.CutoffText="2026-10-27 14:00:00";Check(model.Rows.Count==0&&model.Report is null,"修改知识截止立即清除旧截面");
        await model.ReadAsync();Check(workspace.Cutoff==new DateTimeOffset(2026,10,27,14,0,0,TimeSpan.FromHours(8)),"输入按北京时间解析，不借用操作系统时区");
        workspace.Delayed=true;var old=model.ReadAsync();model.From=model.From!.Value.AddDays(-1);var latest=model.ReadAsync();
        workspace.Replies[1].SetResult(report);await latest;workspace.Replies[0].SetResult(report with{Revisions=[]});await old;
        Check(model.Rows.Count==report.Revisions.Count&&!model.Busy,"旧响应不能覆盖新日期区间或改变新查询的忙碌状态");
        workspace.Delayed=false;workspace.Fail=true;await model.ReadAsync();Check(model.Rows.Count==0&&model.Report is null&&model.Status.Contains("不可用"),"读取失败清除旧事实，保持不可用");
        workspace.Fail=false;var calls=workspace.Calls;model.CutoffText="明天";await model.ReadAsync();Check(workspace.Calls==calls&&model.Rows.Count==0&&model.Status.Contains("yyyy-MM-dd"),"无效截止文本在提交前拒绝");
        model.CutoffText="";workspace.Cancelled=true;await model.ReadAsync();Check(model.Status.Contains("后台采集继续")&&model.Rows.Count==0,"取消只结束查询，不停止后台采集");
    }
    private sealed class Workspace(MarketActivityKnowledgeReport report):ResearchWorkspaceStub
    {
        public int Calls;public bool Delayed,Fail,Cancelled;public DateTimeOffset? Cutoff;public List<TaskCompletionSource<MarketActivityKnowledgeReport>> Replies=[];
        public override Task<MarketActivityKnowledgeReport> MarketActivityKnowledgeAsync(DateOnly from,DateOnly through,DateTimeOffset? cutoff,CancellationToken token)
        {Calls++;Cutoff=cutoff;if(Fail)throw new IOException("fixture unavailable");if(Cancelled)throw new OperationCanceledException();if(!Delayed)return Task.FromResult(report);var source=new TaskCompletionSource<MarketActivityKnowledgeReport>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(source);return source.Task;}
    }
    private static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 成交知识界面："+text);}
}
