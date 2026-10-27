using System.IO;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EtfReportIndexUiChecks
{
    internal static async Task RunAsync()
    {
        var fixture=new Workspace();var model=new EtfHoldingsViewModel(fixture,fixture,fixture,default);
        await model.RefreshCommand.ExecuteAsync(null);
        Check(model.Funds.Count==2&&model.Reports.Count==1&&model.SelectedFund?.Ticker=="510300","仅从用户启用配置建立ETF选择，不另建固定池");
        model.SelectedReport=model.Reports[0];Check(model.RegisterReportCommand.CanExecute(null),"健康目录仍须明确选中报告后才能提交原件登记");
        await model.RegisterReportCommand.ExecuteAsync(null);
        Check(fixture.Registered is {Market:"SSE",Ticker:"510300"}&&model.Status.Contains("不批准持仓"),"登记绑定所选证券及URL，状态不冒充审核完成");
        fixture.Healthy=false;await model.RefreshCommand.ExecuteAsync(null);model.SelectedReport=model.Reports.Single();
        Check(model.Reports.Count==1&&!model.RegisterReportCommand.CanExecute(null)&&model.Status.Contains("不可用"),"相同配置刷新重新检查证据，保留旧线索但禁用登记");
        fixture.Healthy=true;await model.RefreshCommand.ExecuteAsync(null);await model.SearchReportsCommand.ExecuteAsync(null);
        Check(model.Status.Contains("Pending / Accepted")&&model.Status.Contains("受理不等于检索完成")&&model.Reports.Count==1,"受理状态不冒充检索成功或覆盖旧目录");
        fixture.Delayed=true;
        model.SelectedFund=model.Funds[1];var stale=model.LoadTask;
        model.SelectedFund=model.Funds[0];var current=model.LoadTask;
        fixture.Replies[1].Reply.SetResult(Snapshot(fixture.Replies[1].Key,true));await current;
        fixture.Replies[0].Reply.SetResult(Snapshot(fixture.Replies[0].Key,true));await stale;
        Check(model.SelectedFund?.Ticker=="510300"&&model.Reports.All(x=>x.SecurityCodes.Contains("510300")),"迟到的另一ETF目录不能覆盖当前选择");
        fixture.Delayed=false;fixture.NoActive=true;await model.RefreshCommand.ExecuteAsync(null);
        Check(model.SelectedFund is null&&model.Reports.Count==0&&!model.SearchReportsCommand.CanExecute(null),"监控池清空后清除旧证券及旧目录");
        Console.WriteLine("ETF目录ViewModel7项通过，不连接后台。");
    }
    private static EtfReportIndexSnapshot Snapshot(EtfKey key,bool healthy)=>new(key,healthy?"IndexedUnreviewed":"EvidenceUnavailable","synthetic",new(2026,1,1),new(2026,10,27),"2026-10-27T12:00:00.0000000+08:00",1,
        [new("synthetic","合成2026年中期报告","中期报告",new(2026,8,31),"2026-08-31","https://www.sse.com.cn/synthetic.pdf",[key.Ticker])],EtfReportIndexSnapshot.Version);
    private sealed class Workspace:ResearchWorkspaceStub
    {
        public bool Healthy=true,Delayed,NoActive;public AlertDocumentRequest? Registered;
        public List<(EtfKey Key,TaskCompletionSource<EtfReportIndexSnapshot> Reply)> Replies=[];
        public override bool CanManage=>true;
        public override Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>Task.FromResult(new List<EtfConfiguration>{new("SSE","510300","合成ETF甲","fixture",!NoActive,"fixture","fixture",0),new("SZSE","159915","合成ETF乙","fixture",!NoActive,"fixture","fixture",0),new("SSE","510050","合成停用ETF","fixture",false,"fixture","fixture",0)});
        public override Task<EtfReportIndexSnapshot> EtfReportIndexAsync(EtfKey key,CancellationToken token)
        {if(!Delayed)return Task.FromResult(Snapshot(key,Healthy));var reply=new TaskCompletionSource<EtfReportIndexSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add((key,reply));return reply.Task;}
        public override Task<EtfReportSearchReceipt> QueueEtfReportsAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)=>Task.FromResult(new EtfReportSearchReceipt(Guid.NewGuid(),"Pending","Accepted"));
        public override Task RegisterAlertDocumentAsync(AlertDocumentRequest request,CancellationToken token){Registered=request;return Task.CompletedTask;}
    }
    private static void Check(bool ok,string text){if(!ok)throw new InvalidDataException(text);Console.WriteLine("PASS ETF目录界面："+text);}
}
