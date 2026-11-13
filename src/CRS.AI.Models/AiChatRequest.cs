namespace CRS.AI.Models;

/// <summary>单轮已完成的文字上下文。</summary>
public sealed record AiChatContext(string Question, string Answer);

/// <summary>发送当前问题及近期文字上下文。只有当前用户选择的截图才作为附件。</summary>
public sealed record AiChatRequest(string Prompt, IReadOnlyList<AiChatContext> History, byte[]? Screenshot = null)
{
    public const int MaxScreenshotBytes = 1024 * 1024;
}
