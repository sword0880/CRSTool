using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class MarketBreadthModelChecks
{
    public static async Task RunAsync(MarketBreadthHistory history)
    {
        var workspace=new Workspace(history);var model=new MarketBreadthViewModel(workspace,default){Date=history.From.ToDateTime(TimeOnly.MinValue)};
        await model.QueryCommand.ExecuteAsync(null);Check(model.Rows.Count==history.Rows.Count,"手动读取后台档案，不在前端计算广度");
        model.Selected=model.Rows[0];model.Reviewer="fixture";model.ReviewReason="fixture reason";model.OriginalConfirmed=true;model.ReviewReason="changed";
        Check(!model.OriginalConfirmed,"核验说明或选择改变后必须重新确认原件");
        model.OriginalConfirmed=true;await model.AcceptCommand.ExecuteAsync(null);Check(workspace.Reviews==1&&workspace.LastReview?.ExpectedFingerprint==history.StateFingerprint,"审核绑定展示时的完整版本摘要");
        var changes=0;model.Changed+=()=>changes++;model.OriginalPath="synthetic-original";model.NormalizedPath="synthetic-normalized";model.SourceUrl="https://example.invalid/breadth";model.RetrievedText="2026-10-28 14:00:00";model.Submitter="fixture";model.SubmissionReason="fixture";
        model.Date=history.From.AddDays(-1).ToDateTime(TimeOnly.MinValue);await model.SubmitCommand.ExecuteAsync(null);
        Check(workspace.Submissions==1&&model.Date==history.From.ToDateTime(TimeOnly.MinValue)&&model.Rows.Count==history.Rows.Count&&model.Status.Contains("后台已确认登记"),"回执切换统计日后能读取档案，不误取消自身操作");
        Check(changes==1,"资料写入通知父页清除旧周期判断");
        workspace.Allow=false;var writes=workspace.Submissions+workspace.Reviews;await model.SubmitCommand.ExecuteAsync(null);await model.AcceptCommand.ExecuteAsync(null);
        Check(workspace.Submissions+workspace.Reviews==writes&&model.Status.Contains("管理员"),"只读会话即使直接调用命令也不能写入");
        workspace.Allow=true;workspace.Delayed=true;var old=model.QueryCommand.ExecuteAsync(null);model.Date=history.From.AddDays(-2).ToDateTime(TimeOnly.MinValue);workspace.Reply!.SetResult(history);await old;
        Check(model.Rows.Count==0&&model.Selected is null,"改日期后的慢旧响应不覆盖新条件");
        workspace.Delayed=false;workspace.Fail=true;await model.QueryCommand.ExecuteAsync(null);Check(model.Rows.Count==0&&model.Status.Contains("未确认"),"失败不保留旧档案作为当前核验基线");
    }
    private sealed class Workspace(MarketBreadthHistory history):ResearchWorkspaceStub
    {
        public bool Allow=true,Delayed,Fail;public int Submissions,Reviews;public MarketBreadthReview? LastReview;public TaskCompletionSource<MarketBreadthHistory>? Reply;
        public override bool CanManage=>Allow;
        public override Task<MarketBreadthHistory> MarketBreadthHistoryAsync(DateOnly date,CancellationToken token)
        {if(Fail)throw new IOException("fixture unavailable");if(!Delayed)return Task.FromResult(history);Reply=new(TaskCreationOptions.RunContinuationsAsynchronously);return Reply.Task;}
        public override Task<MarketBreadthSubmissionReceipt> SubmitMarketBreadthAsync(string originalPath,string normalizedPath,string sourceUrl,DateTimeOffset retrievedAt,string submitter,string reason,CancellationToken token,IProgress<(int Received,int Total)>? progress=null)
        {Submissions++;return Task.FromResult(new MarketBreadthSubmissionReceipt(Guid.NewGuid(),history.Rows[0].Id,history.From,history.Cutoff,history.Rows[0].OriginalHash,history.Rows[0].NormalizedHash));}
        public override Task ReviewMarketBreadthAsync(MarketBreadthReview review,CancellationToken token){Reviews++;LastReview=review;return Task.CompletedTask;}
    }
    private static void Check(bool value,string text){if(!value)throw new Exception(text);Console.WriteLine("PASS 广度界面状态："+text);}
}
