using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EtfViewModel
{
    private string ComparisonTitle=>AllEtfs?"全部监控ETF（固定当前篮子）":$"{Selected?.Ticker} {Selected?.Name}";
    private async Task<bool> TryRefreshDashboardAsync(CancellationToken token)
    {
        if(workspace is not IEtfDashboardWorkspace reader)return false;
        chartRead?.Cancel();var version=++chartVersion;
        var marketVersion=++activityVersion;
        var key=AllEtfs||Selected is null?null:new EtfKey(Selected.Market,Selected.Ticker);
        var dashboard=await reader.DashboardAsync(key,token);token.ThrowIfCancellationRequested();
        if(dashboard is null)return false;
        var selectedKey=Selected is {} current?new EtfKey(current.Market,current.Ticker):null;
        refreshingRows=true;
        try
        {
            Selected=null;SelectedEvidence=null;InvalidateCoverage();
            Rows.Clear();foreach(var row in dashboard.Overview)Rows.Add(row);
            Selected=Rows.FirstOrDefault(x=>x.Market==selectedKey?.Market&&x.Ticker==selectedKey.Ticker);
        }
        finally{refreshingRows=false;}
        // 读取期间用户可切换证券；汇总结果不能覆盖新选择的图表。
        if(marketVersion==activityVersion)activityPoints=dashboard.Activity;
        if(!AllEtfs&&Selected is null)HistoryLoadTask=LoadHistoryAsync();
        else if(version==chartVersion)ApplyHistory(dashboard.History,ComparisonTitle);
        else RebuildComparison();
        if(marketVersion==activityVersion)ApplyMarketActivityStatus(dashboard.Activity);
        EvidenceLoadTask=LoadEvidenceAsync(token);
        Status=$"当前监控 {Rows.Count} 只 · {workspace.NetworkStatus}";
        await EvidenceLoadTask;return true;
    }
    private void ApplyHistory(IReadOnlyList<EtfHistoryPoint> points,string title)
    {
        historyPoints=points;hasHistory=true;RebuildComparison();
        var latest=points.LastOrDefault();
        ChartStatus=title+(latest is null?" · 暂无历史数据":$" · 截至 {latest.Date:yyyy-MM-dd} · 有效 {latest.Covered}/{latest.Total} 只 · 净变化 {(latest.DeltaShares is {} delta?delta.ToString("+#,##0;-#,##0;0"):"不可计算")}")+"。正值为份额净增加，负值为净减少；折线连接有效观测点，空缺日期不补值；缺份额或日历时净变化不可计算。份额相加不代表资金金额或国家队实际买卖。";
    }
    private void ApplyMarketActivityStatus(IReadOnlyList<MarketActivityPoint> points)
    {
        var last=points.LastOrDefault(x=>x.State=="Complete");
        MarketActivityStatus=last is null?"暂无两市完整成交数据。请采集历史或等待后台任务；单市场缺失、冲突或证据异常时不生成沪深总额。":
            $"最近完整日期 {last.Date:yyyy-MM-dd} · 成交额 {last.AmountYuan/100000000m:N2} 亿元 · 相对前20条完整记录 {(last.ActivityRatio is {} ratio?ratio.ToString("0.00")+"倍":"样本不足")} · 已知不完整/冲突/异常 {points.Count(x=>x.State is not ("Complete" or "NoData" or "Closed"))} 日。";
    }
}
