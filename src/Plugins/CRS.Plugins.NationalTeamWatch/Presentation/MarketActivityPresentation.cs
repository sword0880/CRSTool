using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EtfViewModel
{
    [ObservableProperty] private ISeries[] marketAmountSeries=[];
    [ObservableProperty] private Axis[] marketAmountAxes=[];
    [ObservableProperty] private string marketActivityStatus="等待服务端成交统计；可在任务中心查看独立采集任务。";
    private int activityVersion;
    [RelayCommand] private Task RefreshMarketActivity()=>ExecuteAsync(LoadMarketActivityAsync);
    [RelayCommand] private Task SyncMarketActivity()=>ExecuteAsync(async token=>
    {
        if(SyncFrom is null || SyncTo is null)throw new System.IO.InvalidDataException("请设置采集日期区间。");
        try {await workspace.SyncMarketActivityAsync(DateOnly.FromDateTime(SyncFrom.Value),DateOnly.FromDateTime(SyncTo.Value),token);}
        finally {if(!token.IsCancellationRequested)await LoadMarketActivityAsync(token);}
    });
    private async Task LoadMarketActivityAsync(CancellationToken token)
    {
        var version=++activityVersion;
        activityPoints=[];RebuildComparison();
        MarketActivityStatus="正在加载沪深股票成交统计…";
        try
        {
            var points=await workspace.MarketActivityAsync(token);token.ThrowIfCancellationRequested();
            if(version!=activityVersion)return;
            activityPoints=points;RebuildComparison();
            var last=points.LastOrDefault(x=>x.State=="Complete");
            MarketActivityStatus=last is null?"暂无两市完整成交数据。请采集历史或等待后台任务；单市场缺失、冲突或证据异常时不生成沪深总额。":
                $"最近完整日期 {last.Date:yyyy-MM-dd} · 成交额 {last.AmountYuan/100000000m:N2} 亿元 · 相对前20条完整记录 {(last.ActivityRatio is {} ratio?ratio.ToString("0.00")+"倍":"样本不足")} · 已知不完整/冲突/异常 {points.Count(x=>x.State is not ("Complete" or "NoData"))} 日。";
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception error)
        {
            if(version==activityVersion)MarketActivityStatus="成交统计加载失败，请确认后台已更新且可连接（"+error.GetType().Name+"）。";
        }
    }
}
