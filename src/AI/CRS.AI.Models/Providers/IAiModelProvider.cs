namespace CRS.AI.Models.Providers;

/// <summary>模型供应商统一入口，授权后的工具查询由共享分发器处理。</summary>
internal interface IAiModelProvider
{
    Task<AiModelAnswer> AskChatAsync(AiChatRequest request, CancellationToken token);
    Task TestConnectionAsync(CancellationToken token);
}
