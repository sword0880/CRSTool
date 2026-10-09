using CommunityToolkit.Mvvm.ComponentModel;
using CRS.Application.Abstractions;
using CRS.DesktopClient.Services;

namespace CRS.DesktopClient.ViewModels;

/// <summary>各页面共享当前计算和操作状态，所有金额来自后台。</summary>
public partial class WorkspaceState(IDesktopUseCases useCases, IUserInteraction interaction) : ObservableObject
{
    public IDesktopUseCases UseCases { get; } = useCases;
    public IUserInteraction Interaction { get; } = interaction;
    [ObservableProperty] private int year = DateTime.Today.Year - 1;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string status = "选择年度并导入报表，开始税务测算。";
    [ObservableProperty] private CalculationResult? result;
    public string Fingerprint { get; private set; } = "";
    public IReadOnlyList<ReviewNote> Reviews { get; private set; } = [];
    public bool IsIdle => !IsBusy;
    public bool HasResult => Result is not null;
    public string ResultStatus => Result is { } value
        ? $"{value.Year} 年 · {value.Broker} · 计算{ResultLabels.Calculation(value.CalculationStatus)} · 资料{ResultLabels.Completeness(value.DataCompleteness)} · 对账{ResultLabels.Reconciliation(value.ReconciliationStatus)} · 用途{ResultLabels.Usage(value.UsageLabel)}"
        : "尚未完成年度核算。";
    public int DisplayYear => Result?.Year ?? Year;
    public string AccountScope => Result is null ? "账户范围尚未确认" : Result.CoveredAccounts.Count == 0 ? "账户范围未登记" : string.Join("、", Result.CoveredAccounts);
    public event Action? ResultChanged;

    /// <summary>同步所有页面的可用状态。</summary>
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    /// <summary>展示年度跟随当前结果或输入年度。</summary>
    partial void OnYearChanged(int value) => OnPropertyChanged(nameof(DisplayYear));
    /// <summary>冻结核算状态独立于下载、配置和导出的操作提示。</summary>
    partial void OnResultChanged(CalculationResult? value) => OnPropertyChanged(nameof(ResultStatus));
    /// <summary>输入变更后解除旧结果、复核关联及导出资格。</summary>
    public void Clear()
    {
        Result = null; Fingerprint = ""; Reviews = [];
        OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(DisplayYear)); OnPropertyChanged(nameof(AccountScope));
        ResultChanged?.Invoke();
    }
    /// <summary>先读取对应复核资料，再原子切换各页面的结果。</summary>
    public async Task DisplayAsync(CalculationResult value, string fingerprint = "")
    {
        var notes = await UseCases.ReviewsAsync(value.SnapshotId);
        Reviews = notes; Fingerprint = fingerprint; Result = value;
        Status = ResultStatus;
        OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(DisplayYear)); OnPropertyChanged(nameof(AccountScope));
        ResultChanged?.Invoke();
    }
    /// <summary>防止跨页面重复操作，并统一显示取消和脱敏错误。</summary>
    public async Task<bool> RunAsync(Func<Task> action)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try { await action(); return true; }
        catch (OperationCanceledException) { Status = "操作已取消，未保存半成品计算。"; }
        catch (Exception error)
        {
            UseCases.LogFailure(error.GetType().Name);
            Status = error is CrsException ? error.Message : "操作失败，请检查文件格式、配置和本地文件权限。";
            Interaction.ShowError(Status);
        }
        finally { IsBusy = false; }
        return false;
    }
}
