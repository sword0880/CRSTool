using CommunityToolkit.Mvvm.ComponentModel;

namespace CRS.DesktopClient.ViewModels;

/// <summary>勾选状态只属于界面，任务索引及冻结结果不随选择被修改。</summary>
public partial class HistoryTaskRow : ObservableObject
{
    public HistoryItem Task { get; }
    public bool CanInclude => !Task.IsAnnualAggregate;
    public string TaskKind => Task.IsAnnualAggregate ? "年度汇总" : "账户任务";
    public string BrokerLabel => Task.Broker == "MULTI" ? "多券商" : Task.Broker;
    [ObservableProperty] private bool isIncluded;
    public HistoryTaskRow(HistoryItem task, bool included) { Task = task; isIncluded = included; }
}
