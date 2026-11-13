using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EquityStockListChecks
{
    internal static async Task RunAsync()
    {
        var one=Holding("600036","汇金资管","中央汇金资产管理有限责任公司");
        var two=Holding("600036","社保基金","全国社保基金一零一组合");
        var other=Holding("601398","汇金投资","中央汇金投资有限责任公司");
        var pending=one with{DisclosureId="pending",State="NeedsReview",ReportAsOf=null,Published=null,EvidencePage=null,EvidenceHash=null};
        var notListed=one with{Account="基本养老保险基金一零零一组合",Institution="养老保险基金",Shares=null,State="NotListed"};
        var rows=new[]{one,two,other,pending,notListed};
        var stocks=EquityStockList.Build(rows,true);
        Check(stocks.Count==2&&stocks.Single(x=>x.Ticker=="600036") is {AccountCount:2,InstitutionCount:2},"同股票多个持有人只占一行，未列示和候选不计持有人数");
        Check(EquityStockList.Build(rows,false).Single().Holders.Count==2,"候选与未列示保留在其他披露入口");
        Check(EquityStockList.Build(rows,false).Single().ReportPeriodText.Contains("部分待核验"),"同一股票混有未知报告期时保留缺口说明");
        Check(EquityStockList.Build([one,one with{Name="改名"},one with{Market="SZSE"}],true).Count==2,"按市场和代码分组，不因改名拆股或跨市场合并");
        foreach(var unsafeRow in new[]{pending,notListed,one with{State="Critical"},one with{State="Conflict"},one with{EvidenceHash=""},one with{EvidencePage=null},one with{Institution="主体待核验"},one with{Shares=0},one with{Shares=-1}})
            Check(EquityStockList.Build([unsafeRow],true).Count==0,"证据不足、主体未知、非正股数不进入正式清单："+unsafeRow.State);
        var dates=EquityStockList.Build([one,two with{ReportAsOf=new(2026,3,31)}],true).Single();
        Check(dates.ReportPeriodText=="2026-03-31 至 2026-06-30","不同报告期明确显示范围，不冒充同日总持仓");

        var workspace=new Workspace{Rows=rows.ToList()};var model=new EquityViewModel(workspace,default);
        await model.RefreshAsync();
        Check(model.Stocks.Count==2&&model.SelectedStock is null&&model.StockHolders.Count==0,"首次加载以股票清单为入口，等待用户选择股票");
        model.SelectedStock=model.Stocks.Single(x=>x.Ticker=="600036");await model.DetailTask;
        Check(model.StockHolders.Count==2&&model.Selected?.Ticker=="600036"&&model.DetailTabIndex==0,"点击股票先显示持有人列表");
        Check(workspace.DisclosureReads==0&&model.Events.Count==0&&!model.AdvancedTools&&!model.ShowEquityDetails,"默认直接展示持有人，不额外查询图表、历史与核验资料");
        model.ShowEquityDetails=true;await model.DetailTask;
        Check(workspace.DisclosureReads==1&&model.Events.Count>0,"用户打开更多详情后才读取历史和原件资料");
        model.Selected=model.StockHolders.Single(x=>x.Institution=="社保基金");await model.DetailTask;
        await model.RefreshAsync();await model.DetailTask;
        Check(model.Selected?.Institution=="社保基金"&&model.SelectedStock?.Ticker=="600036","刷新保留股票和具体账户选择");
        model.DetailTabIndex=1;model.OriginalConfirmed=true;
        model.SelectedStock=model.Stocks.Single(x=>x.Ticker=="601398");await model.DetailTask;
        Check(model.StockHolders.Count==1&&model.Selected?.Ticker=="601398"&&model.DetailTabIndex==0&&!model.OriginalConfirmed,"切换股票清理上一账户确认并回到持有人页");
        model.StockListScope=model.StockListScopes[1];await model.DetailTask;
        Check(model.SelectedStock is null&&model.Selected is null&&model.StockHolders.Count==0&&model.Events.Count==0,"切换范围后无同一股票时清空旧详情");
        model.SelectedStock=model.Stocks.Single();await model.DetailTask;
        Check(model.StockHolders.Count==2&&model.StockHolders.All(x=>!EquityStockList.IsVerifiedHolding(x)),"其他披露不混入确认持有人");
        model.Selected=two;await model.DetailTask;
        Check(model.StockListScope==model.StockListScopes[0]&&model.SelectedStock?.Ticker=="600036"&&model.StockHolders.Count==2,"矩阵和具体账户跳转同步股票及证据范围");

        workspace.DelayedTicker="600036";model.SelectedStock=model.Stocks.Single(x=>x.Ticker=="601398");await model.DetailTask;
        model.SelectedStock=model.Stocks.Single(x=>x.Ticker=="600036");var oldDetail=model.DetailTask;
        model.SelectedStock=model.Stocks.Single(x=>x.Ticker=="601398");await model.DetailTask;
        workspace.DisclosureReply.SetResult([]);await oldDetail;
        Check(model.Selected?.Ticker=="601398"&&model.Events.All(x=>x.Ticker=="601398"),"旧股票迟到响应不能覆盖当前持有人详情");
        workspace.DelayedTicker=null;workspace.Rows=[];model.Search="空筛选";await model.LoadTask;await model.DetailTask;
        Check(model.Stocks.Count==0&&model.SelectedStock is null&&model.StockHolders.Count==0&&model.Selected is null&&model.Reports.Count==0,"空筛选清理股票、持有人和报告");
        workspace.Rows=[pending];await model.RefreshAsync();
        Check(model.Stocks.Count==0&&model.StockListStatus.Contains("暂无已核验")&&model.Status.Contains("待核验 1 只"),"只有候选时正式清单保持空并指向待核验入口");
        model.StockListScope=model.StockListScopes[1];model.SelectedStock=model.Stocks.Single();await model.DetailTask;
        workspace.Fail=true;await model.RefreshAsync();await model.DetailTask;
        Check(model.Stocks.Count==0&&model.StockHolders.Count==0&&model.Selected is null&&model.Status.Contains("查询失败"),"查询失败明确报错且不保留旧持有人");
        model.AdvancedTools=true;model.MainTabIndex=1;model.ShowEquityDetails=true;model.DetailTabIndex=1;
        model.ShowEquityDetails=false;model.AdvancedTools=false;
        Check(model.MainTabIndex==0&&model.DetailTabIndex==0&&model.StockListScope==model.StockListScopes[0],"关闭高级工具和更多详情回到简洁入口，不留下隐藏的选择范围");
    }
    private static EquityHoldingRow Holding(string ticker,string institution,string account)=>new("verified-"+ticker,"SSE",ticker,"合成股票"+ticker,institution,account,100,1,new(2026,6,30),new(2026,8,28),new(2026,8,28),"NotComparable",null,"Unknown",false,"https://www.sse.com.cn/fixture.pdf",EvidencePage:"P10",EvidenceHash:new string('a',64));
    private sealed class Workspace:ResearchWorkspaceStub
    {
        public List<EquityHoldingRow> Rows=[];
        public bool Fail;
        public int DisclosureReads;
        public string? DelayedTicker;
        public TaskCompletionSource<List<EquityDisclosure>> DisclosureReply=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<List<EquityWatch>> EquityWatchlistAsync(CancellationToken token)=>Task.FromResult(new List<EquityWatch>());
        public override Task<List<EquityHoldingRow>> EquityHoldingsAsync(string? search,string? institution,string? nature,bool favorites,CancellationToken token)=>Fail?throw new IOException("synthetic unavailable"):Task.FromResult(Rows);
        public override Task<List<EquityDisclosure>> EquityDisclosuresAsync(EtfKey key,CancellationToken token){DisclosureReads++;return key.Ticker==DelayedTicker?DisclosureReply.Task:Task.FromResult(new List<EquityDisclosure>());}
        public override Task<List<EquityHoldingRow>> EquityEventsAsync(EtfKey key,CancellationToken token)=>Task.FromResult(Rows.Where(x=>x.Ticker==key.Ticker).ToList());
        public override Task<List<EquityReportDocument>> EquityReportsAsync(EtfKey key,CancellationToken token)=>Task.FromResult(new List<EquityReportDocument>());
    }
    private static void Check(bool value,string text){if(!value)throw new InvalidDataException(text);Console.WriteLine("PASS 股票清单："+text);}
}
