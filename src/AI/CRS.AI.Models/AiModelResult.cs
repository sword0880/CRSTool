namespace CRS.AI.Models;

/// <summary>记录模型回执中的输入、输出和总 token 用量。</summary>
public sealed record AiTokenUsage(long InputTokens, long OutputTokens, long TotalTokens)
{
    internal AiTokenUsage Add(AiTokenUsage other) =>
        new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens, TotalTokens + other.TotalTokens);
}

/// <summary>一条问题的最终结果，包含答案或拒绝说明以及各轮用量。</summary>
public sealed record AiModelAnswer(string Text, bool Refused, AiTokenUsage Usage, int Requests, int ToolCalls);

/// <summary>向界面提供固定错误码，避免回显上游响应和用户输入。</summary>
public sealed class AiModelException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
