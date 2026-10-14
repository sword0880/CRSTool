using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CRS.DesktopClient.Charts;


namespace CRS.DesktopClient.ViewModels;

/// <summary>仅把后台结果投影到交易、FIFO、税务与图表页面。</summary>
public partial class ResultsViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    [ObservableProperty] private ChartPresentation incomeChart = ChartPresentation.Empty;
    [ObservableProperty] private string estimate = "年度预计补税：不可确定";
    [ObservableProperty] private IReadOnlyList<Trade> trades = [];
    [ObservableProperty] private string tradesHint = "导入并计算报表后查看交易记录。";
    public ObservableCollection<ResultTable> TaxTables { get; } = [];
    public ObservableCollection<ResultTable> FifoTables { get; } = [];
    public ObservableCollection<ResultTable> ReconciliationTables { get; } = [];

    public ResultsViewModel(WorkspaceState state) { State = state; state.ResultChanged += Update; }
    /// <summary>更新只读表格和图表，空结果不会沿用上一份金额。</summary>
    private void Update()
    {
        TaxTables.Clear(); FifoTables.Clear(); ReconciliationTables.Clear();
        Trades = []; IncomeChart = ChartPresentation.Empty; Estimate = "年度预计补税：不可确定";
        if (State.Result is not { } value) { TradesHint = "导入并计算报表后查看交易记录。"; return; }
        foreach (var section in State.UseCases.Sections(value, State.Reviews))
        {
            var table = new ResultTable(section.Title, section.Rows);
            if (section.Title is "FIFO 明细" or "期末 LOT" or "资本收益汇总") FifoTables.Add(table);
            // 抵免表格与税务汇总一起展示，凭证关系可以直接核对。
            else if (section.Title is "税务汇总" or "年度收入主表" or "年度汇总来源" or "抵免国家限额" or "抵免所得分配" or "抵免凭证关系") TaxTables.Add(table);
            else ReconciliationTables.Add(table);
        }
        try
        {
            Trades = State.UseCases.Trades(value);
            TradesHint = value.IsAnnualAggregate ? "年度汇总展示合并收入与收益，交易明细请打开各来源任务查看。"
                : Trades.Count > 0 ? $"当前展示 {Trades.Count} 笔规范化交易，金额与排序来自后台。"
                : "这份历史记录未保存完整交易快照，请重新导入原报表查看交易明细。";
        }
        catch(CrsException error)
        {
            // 快照明细损坏时明确显示原因，不影响冻结汇总，也不沿用上一份交易。
            Trades=[]; TradesHint=error.Message;
        }
        // 顶部金额与税务表、Excel 使用相同状态门槛，不能把部分结果的公式金额当成预计值。
        Estimate = TaxSummaryPresentation.EstimatedAmount(value) is decimal amount ? $"年度预计补税：{amount:N2} 元" : "年度预计补税：不可确定";
        IncomeChart = DesktopChartAdapter.Income(value.Summary);
    }
}
