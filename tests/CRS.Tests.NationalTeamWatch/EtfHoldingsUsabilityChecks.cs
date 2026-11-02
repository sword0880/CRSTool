using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EtfHoldingsUsabilityChecks
{
    internal static async Task RunAsync()
    {
        var fixture=NamedEtfHoldingHistory.Evaluate(NamedEtfHoldingHistoryChecks.Fixture());
        Check(NamedEtfHoldingOverview.Latest(fixture).Count==2,"默认只列最新已核账户，不混入旧期或未列示账户");
        Check(NamedEtfHoldingOverview.Latest(fixture with{Baseline=fixture.Baseline with{State="LatestBaselineUnavailable"}}).Count==0,"最新基准异常时关闭简明清单，不使用健康旧期或部分账户");
        var workspace=new Workspace();var model=new EtfHoldingsViewModel(workspace,workspace,default);model.History.AsOf=new(2026,10,20);
        await model.RefreshCommand.ExecuteAsync(null);
        Check(model.Funds.Count==2&&model.History.CurrentRows.Count==2&&model.ShareSummary?.Ticker=="510300","选择ETF后一次读取份额及机构持仓，无需第二次手动查询");
        Check(workspace.IndexReads==0&&!model.AdvancedTools&&!model.History.ShowDetails,"默认不查询报告目录，不显示核验及排除字段");
        Check(model.Share1Text.StartsWith('+')&&model.Share5Text.StartsWith('-')&&model.Share20Text=="0 份","整体份额增加、减少及真实零变化分别展示");
        Check(model.History.CurrentRows.Single(x=>x.Value.LegalEntityId=="legal-huijin").Direction=="减少"&&model.History.CurrentRows.Any(x=>x.Direction=="首次披露"),"机构方向沿用已核报告期结果，首次披露不说成加仓");
        workspace.Missing5=true;await model.RefreshCommand.ExecuteAsync(null);
        Check(model.Share5Text=="数据不足"&&model.History.CurrentRows.Count==2,"份额窗口缺失保持未知，机构披露可独立查看");
        workspace.Missing5=false;workspace.HistoryFail=true;await model.RefreshCommand.ExecuteAsync(null);
        Check(model.History.CurrentRows.Count==0&&model.History.CurrentStatus.Contains("不可用")&&model.ShareSummary is not null,"机构读取失败不伪造零持仓，整体份额仍可用");
        workspace.HistoryFail=false;workspace.ShareFail=true;await model.RefreshCommand.ExecuteAsync(null);
        Check(model.ShareSummary is null&&model.Share1Text=="数据不足"&&model.History.CurrentRows.Count==2,"份额读取失败不清除有效机构披露，也不显示旧份额");
        workspace.ShareFail=false;workspace.InvalidShare=true;await model.RefreshCommand.ExecuteAsync(null);
        Check(model.ShareSummary is null&&model.Share1Text=="数据不足","份额没有有效日期或总份额时不展示孤立的增减数");
        workspace.InvalidShare=false;model.PreferFund(new("SZSE","159915"));await model.RefreshCommand.ExecuteAsync(null);
        Check(model.SelectedFund?.Ticker=="159915"&&model.ShareSummary?.Ticker=="159915"&&model.History.Report?.Input.Key.Ticker=="159915","沿用ETF份额页所选证券，机构与份额不串ETF");

        workspace.Delayed=true;model.SelectedFund=model.Funds.Single(x=>x.Ticker=="510300");var stale=model.LoadTask;
        model.SelectedFund=model.Funds.Single(x=>x.Ticker=="159915");var current=model.LoadTask;
        workspace.Replies[1].SetResult(workspace.Summaries(77));await current;
        workspace.Replies[0].SetResult(workspace.Summaries(999));await stale;
        Check(model.ShareSummary is{Ticker:"159915",Change1:77}&&model.History.Report?.Input.Key.Ticker=="159915","快速切换ETF时旧份额响应不覆盖新证券");
        model.SelectedFund=model.Funds.Single(x=>x.Ticker=="510300");var canceled=model.LoadTask;model.Cancel();workspace.Replies[^1].SetResult(workspace.Summaries(999));await canceled;
        Check(model.ShareSummary is null&&model.History.CurrentRows.Count==0&&model.ShareStatus.Contains("取消"),"取消页面等待后迟到响应不能重填份额或机构持仓");
        workspace.Delayed=false;model.AdvancedTools=true;await model.LoadTask;
        Check(workspace.IndexReads==1&&model.History.ShowDetails,"主动打开高级工具才读取报告目录和展示历史细节");
        model.SelectedTabIndex=1;model.History.DetailTabIndex=3;model.AdvancedTools=false;
        Check(model.SelectedTabIndex==0&&model.History.DetailTabIndex==0&&!model.History.ShowDetails,"关闭高级工具恢复简明持仓视图");
        workspace.Empty=true;await model.RefreshCommand.ExecuteAsync(null);
        Check(model.SelectedFund is null&&model.ShareSummary is null&&model.History.CurrentRows.Count==0&&model.Reports.Count==0,"监控池清空后两类数据同时清理");
    }
    private sealed class Workspace:ResearchWorkspaceStub
    {
        public bool Missing5,HistoryFail,ShareFail,InvalidShare,Delayed,Empty;public int IndexReads;
        public List<TaskCompletionSource<List<EtfSummary>>> Replies=[];
        public override Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>Task.FromResult(Empty?new List<EtfConfiguration>():[new("SSE","510300","合成沪市ETF","fixture",true,"fixture","fixture",0),new("SZSE","159915","合成深市ETF","fixture",true,"fixture","fixture",0)]);
        public List<EtfSummary> Summaries(decimal delta=10)=>[new("SSE","510300","合成沪市ETF","fixture","市场观察",InvalidShare?null:new(2026,10,20),1000,delta,Missing5?null:-50,0,"synthetic",20),new("SZSE","159915","合成深市ETF","fixture","市场观察",InvalidShare?null:new(2026,10,20),2000,delta,Missing5?null:-50,0,"synthetic",20)];
        public override Task<List<EtfSummary>> OverviewAsync(CancellationToken token)
        {if(ShareFail)throw new IOException("synthetic unavailable");if(!Delayed)return Task.FromResult(Summaries());var reply=new TaskCompletionSource<List<EtfSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(reply);return reply.Task;}
        public override Task<NamedEtfHoldingHistoryReport> EtfHoldingHistoryAsync(EtfKey key,DateOnly asOf,CancellationToken token)
        {
            if(HistoryFail)throw new IOException("synthetic unavailable");var input=NamedEtfHoldingHistoryChecks.Fixture();
            return Task.FromResult(NamedEtfHoldingHistory.Evaluate(input with{Key=key,AsOf=asOf,SnapshotCutoff=new(2026,11,1,10,0,0,TimeSpan.FromHours(8)),Metadata=input.Metadata! with{Market=key.Market,Ticker=key.Ticker},Holdings=input.Holdings.Select(x=>x with{Market=key.Market,Ticker=key.Ticker}).ToArray()}));
        }
        public override Task<EtfReportIndexSnapshot> EtfReportIndexAsync(EtfKey key,CancellationToken token){IndexReads++;return Task.FromResult(new EtfReportIndexSnapshot(key,"NotSearched",null,null,null,null,null,[],EtfReportIndexSnapshot.Version));}
    }
    private static void Check(bool value,string text){if(!value)throw new InvalidDataException(text);Console.WriteLine("PASS ETF简明持仓："+text);}
}
