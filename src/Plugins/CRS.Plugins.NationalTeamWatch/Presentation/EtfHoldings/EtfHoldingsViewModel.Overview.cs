using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>并排显示后台份额指标与机构披露，不计算或合并机构交易方向。</summary>
public partial class EtfHoldingsViewModel
{
    [ObservableProperty] private bool advancedTools;
    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(Share1Text)),NotifyPropertyChangedFor(nameof(Share5Text)),NotifyPropertyChangedFor(nameof(Share20Text))]
    private EtfSummary? shareSummary;
    [ObservableProperty] private string shareStatus="选择ETF后自动读取整体份额及机构披露。";
    private EtfKey? preferredFund;
    public string Share1Text=>ShareDelta(ShareSummary?.Change1);
    public string Share5Text=>ShareDelta(ShareSummary?.Change5);
    public string Share20Text=>ShareDelta(ShareSummary?.Change20);
    private static string ShareDelta(decimal? value)=>value is {} number?number.ToString("+#,##0.############################;-#,##0.############################;0")+" 份":"数据不足";
    public void PreferFund(EtfKey? key){if(key is not null)preferredFund=key;}
    partial void OnAdvancedToolsChanged(bool value)
    {
        History.ShowDetails=value;
        if(!value){SelectedTabIndex=0;return;}
        if(SelectedFund is {} fund)LoadTask=RunAsync(token=>ReadFundAsync(fund,token));
    }
    private async Task ReadFundAsync(EtfConfiguration fund,CancellationToken token)
    {
        await Task.WhenAll(History.ReadAsync(),ReadSharesAsync(fund,token));
        token.ThrowIfCancellationRequested();
        if(AdvancedTools)await ReadIndexAsync(fund,token);
    }
    private async Task ReadSharesAsync(EtfConfiguration fund,CancellationToken token)
    {
        ShareSummary=null;ShareStatus="正在读取ETF整体份额…";
        try
        {
            var rows=await etfs.OverviewAsync(token);token.ThrowIfCancellationRequested();
            if(SelectedFund?.Market!=fund.Market||SelectedFund.Ticker!=fund.Ticker)return;
            var selected=rows.SingleOrDefault(x=>x.Market==fund.Market&&x.Ticker==fund.Ticker);
            ShareSummary=selected is {AsOf:not null,Shares:>=0}?selected:null;
            ShareStatus=ShareSummary is {AsOf:{} date,Shares:{} shares}?$"ETF整体份额 · 截至 {date:yyyy-MM-dd} · 总份额 {shares:N0} 份":"ETF整体份额暂无有效数据，不能判断份额增减。";
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception)
        {
            if(token.IsCancellationRequested||SelectedFund?.Market!=fund.Market||SelectedFund.Ticker!=fund.Ticker)return;
            ShareSummary=null;ShareStatus="ETF份额暂不可用，请稍后刷新。";
        }
    }
}
