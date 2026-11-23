using CRS.Application.Abstractions;

namespace CRS.Infrastructure;

public sealed partial class LocalStore
{
    /// <summary>固定绑定当前解锁会话，旧的仓储实例不能在重新解锁后继续访问。</summary>
    public IAiChatHistory CreateAiChatHistory() => new AiChatHistoryStore(session);
}
