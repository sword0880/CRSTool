using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels.AI;

/// <summary>聊天历史只读取当前保险库，切换搜索或选择时丢弃晚到结果。</summary>
public sealed partial class AiChatHistoryViewModel(IAiChatHistory? history) : ObservableObject
{
    private CancellationTokenSource? pending;
    private bool locked;
    [ObservableProperty] private string query = "";
    [ObservableProperty] private IReadOnlyList<AiChatSummary> items = [];
    [ObservableProperty] private AiChatSummary? selected;
    [ObservableProperty] private AiChatConversation? conversation;
    [ObservableProperty] private string status = "";

    partial void OnQueryChanged(string value) => _ = RefreshAsync(debounce: true);
    partial void OnSelectedChanged(AiChatSummary? value)
    {
        Conversation = null;
        if (value is not null) _ = ReadAsync(value.Id);
    }

    public Task RefreshAsync() => RefreshAsync(debounce: false);

    private Task RefreshAsync(bool debounce) => RunAsync(async token =>
    {
        if (debounce) await Task.Delay(250, token);
        var list = await history!.SearchAsync(Query, token);
        token.ThrowIfCancellationRequested();
        Items = list;
        Status = list.Count == 0 ? "暂无匹配的聊天记录。" : "";
    });

    private Task ReadAsync(Guid id) => RunAsync(async token =>
    {
        var result = await history!.ReadAsync(id, token);
        token.ThrowIfCancellationRequested();
        Conversation = result;
    });

    public async Task<bool> DeleteAsync(Guid id)
    {
        var deleted = false;
        await RunAsync(async token =>
        {
            await history!.DeleteAsync(id, token);
            token.ThrowIfCancellationRequested();
            Selected = null;
            Conversation = null;
            var list = await history.SearchAsync(Query, token);
            token.ThrowIfCancellationRequested();
            Items = list;
            Status = list.Count == 0 ? "暂无匹配的聊天记录。" : "";
            deleted = true;
        });
        return deleted;
    }

    public void ClearForLock()
    {
        locked = true;
        pending?.Cancel();
        Query = "";
        Selected = null;
        Conversation = null;
        Items = [];
        Status = "";
    }

    private async Task RunAsync(Func<CancellationToken, Task> run)
    {
        if (locked) return;
        if (history is null) { Status = "当前保险库无法使用聊天历史。"; return; }
        pending?.Cancel();
        using var cancellation = new CancellationTokenSource();
        pending = cancellation;
        try { await run(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (AiAssistantViewModel.IsStorageError(error))
        { if (!cancellation.IsCancellationRequested) Status = "无法读取或修改聊天历史，请重试。"; }
        finally { if (ReferenceEquals(pending, cancellation)) pending = null; }
    }
}
