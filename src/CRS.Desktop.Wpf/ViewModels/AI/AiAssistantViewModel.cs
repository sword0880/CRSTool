using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.AI.Models;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels.AI;

/// <summary>用已保存的默认模型回答公开 ETF 问题，锁定时取消并清理当前内容。</summary>
public sealed partial class AiAssistantViewModel(IAiModelService? service, AiModelSettingsViewModel settings, IAiChatHistory? history)
    : ObservableObject
{
    private CancellationTokenSource? request;
    private Guid id = Guid.NewGuid();
    private long revision;
    private (string Provider, string Model)? conversationModel;
    private bool locked;
    public Guid CurrentId => id;
    public ObservableCollection<AiChatEntry> Entries { get; } = [];
    public string DefaultModelLabel => settings.DefaultLabel;
    public bool SupportsScreenshot => settings.DefaultSelection is { } choice &&
        AiModelSettings.SupportsScreenshots(choice.Provider, choice.Model);
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AskCommand))] private string prompt = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasScreenshot))] private byte[]? screenshot;
    [ObservableProperty] private bool keepScreenshot;
    [ObservableProperty] private string answer = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string usage = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanEditDraft)), NotifyCanExecuteChangedFor(nameof(AskCommand)),
     NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool busy;

    public bool HasScreenshot => Screenshot is not null;
    public bool CanEditDraft => !Busy && !locked;

    public void RefreshModel()
    {
        OnPropertyChanged(nameof(DefaultModelLabel));
        OnPropertyChanged(nameof(SupportsScreenshot));
    }

    public void AttachScreenshot(byte[] image)
    {
        if (Busy || locked) return;
        if (!SupportsScreenshot) { Status = "当前模型不支持截图，请在设置中更换模型。"; return; }
        Screenshot = image;
        KeepScreenshot = false;
        Status = "";
    }

    private bool CanAsk() => service is not null && !locked && !Busy && !string.IsNullOrWhiteSpace(Prompt);
    private bool CanCancel() => Busy;
    private bool CanNewChat() => !Busy && !locked;

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync()
    {
        using var cancel = new CancellationTokenSource();
        request = cancel;
        Busy = true;
        Status = "正在请求模型…";
        Answer = Usage = "";
        var question = Prompt.Trim();
        var image = Screenshot;
        var preserveImage = KeepScreenshot;
        try
        {
            if (settings.DefaultSelection is not { } selected) throw new AiModelException("DefaultModelMissing");
            // 会话不自动把旧消息发送给新的供应商或模型。
            if (conversationModel is { } previous && previous != selected)
            {
                Status = "当前默认模型与此会话不同。请切回原模型，或新建对话。";
                return;
            }
            var result = await service!.AskChatAsync(selected.Provider, selected.Model,
                new(question, RecentContext(), image), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            var entry = new AiChatEntry(question, result.Text, preserveImage && history is not null ? image : null);
            // 保存失败仍显示答案，但此会话停止追加，以免留下不连续的历史。
            try
            {
                if (history is not null && revision >= 0)
                    revision = await history.AppendAsync(id, selected.Provider, selected.Model, revision, entry, cancel.Token);
            }
            catch (Exception error) when (!cancel.IsCancellationRequested && IsStorageError(error))
            {
                revision = -1;
            }
            cancel.Token.ThrowIfCancellationRequested();
            conversationModel = selected;
            Entries.Add(entry);
            Answer = result.Text;
            if (revision >= 0) Status = result.Refused ? "模型拒绝了本次请求。" : "";
            else Status = "回答已收到，但此会话无法保存。可复制内容后新建对话。";
            Prompt = "";
            ClearScreenshot();
            Usage = $"输入 {result.Usage.InputTokens} · 输出 {result.Usage.OutputTokens} tokens · 工具调用 {result.ToolCalls} 次";
        }
        catch (AiModelException error) when (!cancel.IsCancellationRequested) { Status = AiModelMessages.Explain(error); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (AiModelException) when (cancel.IsCancellationRequested) { }
        catch (Exception error) when (cancel.IsCancellationRequested && IsStorageError(error)) { }
        finally { request = null; Busy = false; }
    }

    partial void OnBusyChanged(bool value)
    {
        NewChatCommand.NotifyCanExecuteChanged();
        RemoveScreenshotCommand.NotifyCanExecuteChanged();
    }

    // 只回传最近的完整文字问答，历史附件留在本机。
    private IReadOnlyList<AiChatContext> RecentContext()
    {
        var result = new List<AiChatContext>();
        var length = 0;
        foreach (var entry in Entries.Reverse().Take(8))
        {
            length += entry.Question.Length + entry.Answer.Length;
            if (length > 16000) break;
            result.Add(new(entry.Question, entry.Answer));
        }
        result.Reverse();
        return result;
    }

    [RelayCommand(CanExecute = nameof(CanNewChat))]
    private void NewChat() => Reset();

    [RelayCommand(CanExecute = nameof(CanNewChat))]
    private void RemoveScreenshot() => ClearScreenshot();

    private void ClearScreenshot() { Screenshot = null; KeepScreenshot = false; }

    public bool Resume(AiChatConversation conversation)
    {
        if (Busy || locked) return false;
        Reset();
        id = conversation.Id;
        revision = conversation.Revision;
        conversationModel = (conversation.Provider, conversation.Model);
        foreach (var entry in conversation.Entries) Entries.Add(entry);
        if (settings.DefaultSelection != conversationModel)
            Status = $"此会话使用 {conversation.Model}。请在设置中切回该模型后继续。";
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        request?.Cancel();
        Status = "已取消本次请求。";
    }

    public void ClearForLock()
    {
        locked = true;
        request?.Cancel();
        Reset();
        AskCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        RemoveScreenshotCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditDraft));
    }

    private void Reset()
    {
        id = Guid.NewGuid();
        revision = 0;
        conversationModel = null;
        Entries.Clear();
        ClearScreenshot();
        Prompt = Answer = Status = Usage = "";
    }

    internal static bool IsStorageError(Exception error) =>
        error is CrsException or System.Data.Common.DbException or System.IO.IOException or UnauthorizedAccessException or FormatException;
}
