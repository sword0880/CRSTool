// 市场整体成交的图表展示与筛选状态；保留未覆盖和不可计算区间，市场冷热度不归因为国家队成交。
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EtfViewModel
{
    public System.Collections.ObjectModel.ObservableCollection<CRS.Plugins.NationalTeamWatch.Application.MarketActivityRevision> ActivityRevisions {get;}=[];
    [ObservableProperty] private CRS.Plugins.NationalTeamWatch.Application.MarketActivityRevision? selectedActivityRevision;
    [RelayCommand] private Task RefreshActivityRevisions()=>ExecuteAsync(async token=>{var rows=await marketCycleWorkspace.MarketActivityRevisionsAsync(token);SelectedActivityRevision=null;ActivityRevisions.Clear();foreach(var row in rows)ActivityRevisions.Add(row);});
    [RelayCommand] private Task AcceptActivityRevision()=>ReviewActivityRevision(true);
    [RelayCommand] private Task RejectActivityRevision()=>ReviewActivityRevision(false);
    private Task ReviewActivityRevision(bool accept)=>ExecuteAsync(async token=>
    {
        if(SelectedActivityRevision is null)throw new System.IO.InvalidDataException("请先选择成交统计修订。");
        await marketCycleWorkspace.ReviewMarketActivityAsync(SelectedActivityRevision.Id,accept,Reviewer,token);
        var rows=await marketCycleWorkspace.MarketActivityRevisionsAsync(token);SelectedActivityRevision=null;ActivityRevisions.Clear();foreach(var row in rows)ActivityRevisions.Add(row);
        await LoadMarketActivityAsync(token);
    });
    [ObservableProperty] private ISeries[] marketAmountSeries=[];
    [ObservableProperty] private Axis[] marketAmountAxes=[];
    [ObservableProperty] private string marketActivityStatus="等待服务端成交统计；可在任务中心查看独立采集任务。";
    private int activityVersion;
    [RelayCommand] private Task RefreshMarketActivity()=>ExecuteAsync(LoadMarketActivityAsync);
    [RelayCommand] private Task SyncMarketActivity()=>ExecuteAsync(async token=>
    {
        if(SyncFrom is null || SyncTo is null)throw new System.IO.InvalidDataException("请设置采集日期区间。");
        var from=DateOnly.FromDateTime(SyncFrom.Value);
        var to=DateOnly.FromDateTime(SyncTo.Value);
        var range=$"{from:yyyy-MM-dd} 至 {to:yyyy-MM-dd}";
        MarketActivityStatus=$"正在向后台提交 {range} 的沪深成交额采集，并等待执行结果；实际来源限流可能导致任务延期或失败。";
        try
        {
            await marketCycleWorkspace.SyncMarketActivityAsync(from,to,token);
            token.ThrowIfCancellationRequested();
            await LoadMarketActivityAsync(token);
            token.ThrowIfCancellationRequested();
            var result=MarketActivityStatus;
            MarketActivityStatus=$"后台报告本轮采集已完成（{range}）。{result} " +
                "成功不代表新增记录：原始记录可能已存在；实际请求数与失败原因请在“任务中心 → 执行记录”查看该次任务的检查点与日志。";
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {
            MarketActivityStatus="已停止等待采集；后台已接受的任务可能继续执行，请查看任务中心的执行记录。";
            throw;
        }
        catch(Exception error)
        {
            var reason=error is System.IO.InvalidDataException or MissingMethodException ? error.Message : error.GetType().Name;
            MarketActivityStatus=$"本轮成交额采集未确认成功（{range}）：{reason}。请到“任务中心 → 执行记录”查看 market-activity-sync 的来源请求和失败检查点；冷却期间不会强行再次访问官网。";
            throw;
        }
    });
    private async Task LoadMarketActivityAsync(CancellationToken token)
    {
        var version=++activityVersion;
        activityPoints=[];RebuildComparison();
        MarketActivityStatus="正在加载沪深股票成交统计…";
        try
        {
            var points=await marketCycleWorkspace.MarketActivityAsync(token);token.ThrowIfCancellationRequested();
            if(version!=activityVersion)return;
            activityPoints=points;RebuildComparison();
            ApplyMarketActivityStatus(points);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){ }
        catch(Exception error)
        {
            if(version==activityVersion)MarketActivityStatus="成交统计加载失败，请确认后台已更新且可连接（"+error.GetType().Name+"）。";
        }
    }
}
