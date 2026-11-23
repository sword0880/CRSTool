using CRS.AI.Models.Attachments;

namespace CRS.AI.Models;

/// <summary>单轮已完成的文字上下文。</summary>
public sealed record AiChatContext(string Question, string Answer);

/// <summary>发送当前问题、近期文字上下文、截图及当前会话内存附件。</summary>
public sealed record AiChatRequest(
    string Prompt,
    IReadOnlyList<AiChatContext> History,
    byte[]? Screenshot = null,
    IReadOnlyList<AiAttachmentDocument>? Attachments = null)
{
    public const int MaxScreenshotBytes = 1024 * 1024;
}
