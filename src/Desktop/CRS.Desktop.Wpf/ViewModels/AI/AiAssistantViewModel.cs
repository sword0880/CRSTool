using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.AI.Models;
using CRS.AI.Models.Attachments;
using CRS.Application.Abstractions;

namespace CRS.DesktopClient.ViewModels.AI;

/// <summary>管理当前 AI 会话；附件解析文本只保留在此内存会话中，不写入聊天历史。</summary>
public sealed partial class AiAssistantViewModel(IAiModelService? service, AiModelSettingsViewModel settings, IAiChatHistory? history)
    : ObservableObject
{
    private CancellationTokenSource? request;
    private CancellationTokenSource? attachmentRead;
    private readonly HashSet<Guid> pendingAttachmentIds = [];
    private Guid id = Guid.NewGuid();
    private long revision;
    private (string Provider, string Model)? conversationModel;
    private bool locked;

    public Guid CurrentId => id;
    public ObservableCollection<AiChatEntry> Entries { get; } = [];
    public ObservableCollection<AiAttachmentDocument> Attachments { get; } = [];
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
     NotifyCanExecuteChangedFor(nameof(CancelCommand)), NotifyCanExecuteChangedFor(nameof(NewChatCommand)),
     NotifyCanExecuteChangedFor(nameof(RemoveScreenshotCommand)), NotifyCanExecuteChangedFor(nameof(RemoveAttachmentCommand))]
    private bool busy;

    public bool HasScreenshot => Screenshot is not null;
    public bool HasAttachments => Attachments.Count > 0;
    public bool CanEditDraft => !Busy && !locked && attachmentRead is null;

    public void RefreshModel()
    {
        OnPropertyChanged(nameof(DefaultModelLabel));
        OnPropertyChanged(nameof(SupportsScreenshot));
    }

    public void AttachScreenshot(byte[] image)
    {
        if (!CanEditDraft) return;
        if (!SupportsScreenshot) { Status = "当前模型不支持截图，请在设置中更换模型。"; return; }
        Screenshot = image;
        KeepScreenshot = false;
        Status = "";
    }

    /// <summary>读取多个用户文件，立即校验并解析；原始字节只在本方法中短暂存在。</summary>
    public async Task AddAttachmentsAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        if (!CanEditDraft) return;
        var selected = paths.Take(AiAttachmentParser.MaxFiles + 1).ToArray();
        if (selected.Length == 0) return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        attachmentRead = cancellation;
        Status = "正在读取和检查附件…";
        OnPropertyChanged(nameof(CanEditDraft));
        AskCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
        try
        {
            foreach (var path in selected)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (locked) return;
                if (Attachments.Count >= AiAttachmentParser.MaxFiles)
                {
                    Status = "当前会话最多保留 5 个附件。";
                    break;
                }

                AiAttachmentDocument? document = null;
                try
                {
                    document = await AiAttachmentParser.ReadFileAsync(path, cancellation.Token).ConfigureAwait(true);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (locked) return;
                    if (Attachments.Any(item => item.Sha256 == document.Sha256))
                    {
                        document.Dispose();
                        document = null;
                        Status = "相同内容的附件已经添加，已跳过重复文件。";
                        continue;
                    }

                    AiAttachmentParser.ValidateCollection(Attachments.Append(document).ToArray());
                    Attachments.Add(document);
                    pendingAttachmentIds.Add(document.Id);
                    document = null;
                    OnPropertyChanged(nameof(HasAttachments));
                }
                catch (InvalidDataException error)
                {
                    Status = "附件未添加：" + error.Message;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    Status = "无法读取附件，请检查文件是否被占用及当前用户的访问权限。";
                }
                finally
                {
                    document?.Dispose();
                }
            }
            if (Status == "正在读取和检查附件…") Status = "";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = "附件读取已取消。";
        }
        finally
        {
            attachmentRead = null;
            OnPropertyChanged(nameof(CanEditDraft));
            AskCommand.NotifyCanExecuteChanged();
            NewChatCommand.NotifyCanExecuteChanged();
            RemoveAttachmentCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanAsk() => service is not null && !locked && !Busy && attachmentRead is null &&
        !string.IsNullOrWhiteSpace(Prompt);
    private bool CanCancel() => Busy;
    private bool CanNewChat() => !Busy && !locked && attachmentRead is null;
    private bool CanEditAttachments() => CanEditDraft;

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
        var attachments = Attachments.ToArray();
        var newlyAttached = attachments.Where(item => pendingAttachmentIds.Contains(item.Id)).ToArray();
        var storedQuestion = newlyAttached.Length == 0
            ? question
            : question + Environment.NewLine + Environment.NewLine + "[本条消息附件：" +
              string.Join("、", newlyAttached.Select(item => item.FileName)) + "]";

        try
        {
            AiAttachmentParser.ValidateCollection(attachments);
            if (settings.DefaultSelection is not { } selected) throw new AiModelException("DefaultModelMissing");
            if (conversationModel is { } previous && previous != selected)
            {
                Status = "当前默认模型与此会话不同。请切回原模型，或新建对话。";
                return;
            }
            var result = await service!.AskChatAsync(selected.Provider, selected.Model,
                new(question, RecentContext(), image, attachments), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            var entry = new AiChatEntry(storedQuestion, result.Text, preserveImage && history is not null ? image : null);
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
            pendingAttachmentIds.Clear();
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
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
    }

    // 只回传最近的完整文字问答；附件正文由当前内存会话单独提供。
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

    [RelayCommand(CanExecute = nameof(CanEditAttachments))]
    private void RemoveScreenshot() => ClearScreenshot();

    [RelayCommand(CanExecute = nameof(CanEditAttachments))]
    private void RemoveAttachment(Guid attachmentId)
    {
        var document = Attachments.FirstOrDefault(item => item.Id == attachmentId);
        if (document is null) return;
        Attachments.Remove(document);
        pendingAttachmentIds.Remove(attachmentId);
        document.Dispose();
        OnPropertyChanged(nameof(HasAttachments));
        Status = "";
    }

    private void ClearScreenshot() { Screenshot = null; KeepScreenshot = false; }

    public bool Resume(AiChatConversation conversation)
    {
        if (Busy || locked || attachmentRead is not null) return false;
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
        attachmentRead?.Cancel();
        Reset();
        AskCommand.NotifyCanExecuteChanged();
        NewChatCommand.NotifyCanExecuteChanged();
        RemoveScreenshotCommand.NotifyCanExecuteChanged();
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditDraft));
    }

    private void Reset()
    {
        id = Guid.NewGuid();
        revision = 0;
        conversationModel = null;
        Entries.Clear();
        ClearScreenshot();
        foreach (var attachment in Attachments) attachment.Dispose();
        Attachments.Clear();
        pendingAttachmentIds.Clear();
        OnPropertyChanged(nameof(HasAttachments));
        Prompt = Answer = Status = Usage = "";
    }

    internal static bool IsStorageError(Exception error) =>
        error is CrsException or System.Data.Common.DbException or System.IO.IOException or UnauthorizedAccessException or FormatException;
}
