using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.AI.Models;

namespace CRS.DesktopClient.ViewModels.AI;

/// <summary>用已保存的默认模型回答公开 ETF 问题，锁定时取消并清理当前内容。</summary>
public sealed partial class AiAssistantViewModel(IAiModelService? service, AiModelSettingsViewModel settings) : ObservableObject
{
    private CancellationTokenSource? request;
    public string DefaultModelLabel => settings.DefaultLabel;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AskCommand))] private string prompt = "";
    [ObservableProperty] private string answer = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string usage = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AskCommand)),
     NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool busy;

    public void RefreshModel() => OnPropertyChanged(nameof(DefaultModelLabel));

    private bool CanAsk() => service is not null && !Busy && !string.IsNullOrWhiteSpace(Prompt);
    private bool CanCancel() => Busy;

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        using var cancel = new CancellationTokenSource();
        request = cancel;
        Busy = true;
        Status = "正在请求模型…";
        Answer = Usage = "";
        try
        {
            if (settings.DefaultSelection is not { } selected) throw new AiModelException("DefaultModelMissing");
            var result = await service!.AskDefaultAsync(selected.Provider, selected.Model, Prompt, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            Answer = result.Text;
            Status = result.Refused ? "模型拒绝了本次请求。" : "";
            Usage = $"输入 {result.Usage.InputTokens} · 输出 {result.Usage.OutputTokens} tokens · 工具调用 {result.ToolCalls} 次";
        }
        catch (AiModelException error) when (!cancel.IsCancellationRequested) { Status = AiModelMessages.Explain(error); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (AiModelException) when (cancel.IsCancellationRequested) { }
        finally { request = null; Busy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        request?.Cancel();
        Status = "已取消本次请求。";
    }

    public void ClearForLock()
    {
        request?.Cancel();
        Prompt = Answer = Status = Usage = "";
    }
}
