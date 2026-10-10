using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace CRS.DesktopClient.ViewModels;

/// <summary>仅把后台结果投影到交易、FIFO、税务与图表页面。</summary>
public partial class ResultsViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    [ObservableProperty] private ISeries[] incomeSeries = [];
    [ObservableProperty] private string estimate = "年度预计补税：不可确定";
    [ObservableProperty] private IReadOnlyList<Trade> trades = [];
    [ObservableProperty] private string tradesHint = "导入并计算报表后查看交易记录。";
    public ObservableCollection<ResultTable> TaxTables { get; } = [];
    public ObservableCollection<ResultTable> FifoTables { get; } = [];
    public ObservableCollection<ResultTable> ReconciliationTables { get; } = [];
    public Axis[] IncomeAxes { get; } = [new Axis { Labels = ["股息", "利息", "证券收益"], Name = "收入项目" }];
    public Axis[] MoneyAxes { get; } = [new Axis { Name = "人民币", Labeler = value => value.ToString("N0") }];

    public ResultsViewModel(WorkspaceState state) { State = state; state.ResultChanged += Update; }
    /// <summary>更新只读表格和图表，空结果不会沿用上一份金额。</summary>
    private void Update()
    {
        TaxTables.Clear(); FifoTables.Clear(); ReconciliationTables.Clear();
        Trades = []; IncomeSeries = []; Estimate = "年度预计补税：不可确定";
        if (State.Result is not { } value) { TradesHint = "导入并计算报表后查看交易记录。"; return; }
        foreach (var section in State.UseCases.Sections(value, State.Reviews))
        {
            var table = new ResultTable(section.Title, section.Rows);
            if (section.Title is "FIFO 明细" or "期末 LOT" or "资本收益汇总") FifoTables.Add(table);
            // 抵免表格与税务汇总一起展示，凭证关系可以直接核对。
            else if (section.Title is "税务汇总" or "年度收入主表" or "年度汇总来源" or "抵免国家限额" or "抵免所得分配" or "抵免凭证关系") TaxTables.Add(table);
            else ReconciliationTables.Add(table);
        }
        Trades = State.UseCases.Trades(value);
        TradesHint = value.IsAnnualAggregate ? "年度汇总展示合并收入与收益，交易明细请打开各来源任务查看。"
            : Trades.Count > 0 ? $"当前展示 {Trades.Count} 笔规范化交易，金额与排序来自后台。"
            : "这份历史记录未保存完整交易快照，请重新导入原报表查看交易明细。";
        Estimate = value.EstimatedTopUpCny is decimal amount ? $"年度预计补税：{amount:N2} 元" : "年度预计补税：不可确定";
        IncomeSeries = [new ColumnSeries<decimal> { Name = "收入（人民币）", Values = new[] { value.Summary.DividendCny, value.Summary.InterestCny, value.Summary.GainCny } }];
    }
}
