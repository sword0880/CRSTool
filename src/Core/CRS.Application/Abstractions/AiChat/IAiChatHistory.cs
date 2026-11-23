namespace CRS.Application.Abstractions;

/// <summary>聊天记录只含可见问答和用户选择保留的截图。</summary>
public sealed record AiChatEntry(string Question, string Answer, byte[]? Screenshot = null);
public sealed record AiChatSummary(Guid Id, string Title, DateTimeOffset UpdatedAt, string Model);
public sealed record AiChatConversation(Guid Id, string Provider, string Model, long Revision, IReadOnlyList<AiChatEntry> Entries);

/// <summary>当前解锁保险库的聊天历史。每次追加一组完整问答。</summary>
public interface IAiChatHistory
{
    Task<IReadOnlyList<AiChatSummary>> SearchAsync(string query, CancellationToken token);
    Task<AiChatConversation?> ReadAsync(Guid id, CancellationToken token);
    Task<long> AppendAsync(Guid id, string provider, string model, long revision, AiChatEntry entry, CancellationToken token);
    Task DeleteAsync(Guid id, CancellationToken token);
}
