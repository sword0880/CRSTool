using System.IO;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EquityDirectionModelChecks
{
    internal static async Task RunAsync(EquityDirectionReport report)
    {
        var workspace=new Workspace(report);var model=new EquityViewModel(workspace,default);
        await model.LoadDirectionAsync();Check(model.DirectionGroups.Count==report.Groups.Count&&model.DirectionPositions.Count==report.Positions.Count&&model.DirectionStatus.Contains("仅为已披露样本"),"展示后台样本和覆盖，不在界面计算行业权重");
        model.SelectedDirectionPosition=model.DirectionPositions.First();model.DirectionOriginalConfirmed=true;model.DirectionSectorName="新核验名称";
        Check(!model.DirectionOriginalConfirmed,"修改行业或价格后原文确认立即失效");
        model.DirectionOriginalConfirmed=true;model.DirectionDate=new(2026,3,31);
        Check(model.DirectionPositions.Count==0&&model.DirectionGroups.Count==0&&!model.DirectionOriginalConfirmed,"修改报告期立即清除旧数据及确认");
        workspace.Delayed=true;var old=model.LoadDirectionAsync();model.DirectionDate=new(2026,6,30);var current=model.LoadDirectionAsync();
        workspace.Replies[1].SetResult(report);await current;workspace.Replies[0].SetResult(report with{Groups=[]});await old;
        Check(model.DirectionGroups.Count==report.Groups.Count,"迟到的旧报告期响应不能清空当前报告");
        workspace.Delayed=false;workspace.Fail=true;await model.LoadDirectionAsync();
        Check(model.DirectionGroups.Count==0&&model.DirectionPositions.Count==0&&model.DirectionStatus.Contains("不可用"),"读取失败移除旧权重并明确不可用");
        workspace.Fail=false;workspace.Manage=false;await model.LoadDirectionAsync();model.SelectedDirectionPosition=model.DirectionPositions.First();model.DirectionOriginalConfirmed=true;
        await model.ReviewDirectionCommand.ExecuteAsync(null);
        Check(workspace.Reviews==0&&!model.DirectionCanEdit&&model.DirectionIsIdle&&model.DirectionPositions.Count>0,"普通读取者可选报告和持仓，但不能提交审核");
        workspace.Manage=true;model.SelectedDirectionPosition=null;model.SelectedDirectionPosition=model.DirectionPositions.First();model.DirectionReviewer="synthetic-ui-reviewer";model.DirectionReason="独立界面核验";model.DirectionOriginalConfirmed=true;
        await model.ReviewDirectionCommand.ExecuteAsync(null);
        Check(workspace.Reviews==1&&workspace.Last?.DisclosureId==report.Positions.First().Holding.DisclosureId&&workspace.Last.PreviousReviewId==report.Positions.First().Fact?.ReviewId&&!model.DirectionOriginalConfirmed,"登记冻结所选披露、原件与核验基线，完成后重新读取");
    }
    private sealed class Workspace(EquityDirectionReport report):ResearchWorkspaceStub
    {
        public bool Delayed,Fail,Manage=true;public int Reviews;public EquityDirectionReview? Last;public List<TaskCompletionSource<EquityDirectionReport>> Replies=[];
        public override bool CanManage=>Manage;
        public override Task<EquityDirectionReport> EquityDirectionAsync(DateOnly? reportAsOf,CancellationToken token)
        {if(Fail)throw new IOException("synthetic unavailable");if(!Delayed)return Task.FromResult(report);var reply=new TaskCompletionSource<EquityDirectionReport>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(reply);return reply.Task;}
        public override Task ReviewEquityDirectionAsync(EquityDirectionReview review,CancellationToken token){Reviews++;Last=review;return Task.CompletedTask;}
    }
    private static void Check(bool value,string text){if(!value)throw new InvalidDataException(text);Console.WriteLine("PASS 方向界面："+text);}
}
