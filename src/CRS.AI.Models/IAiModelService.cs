namespace CRS.AI.Models;

/// <summary>桌面模型配置及公开 ETF 问答入口。界面只接收配置状态和结果。</summary>
public interface IAiModelService
{
    AiModelSettings LoadSettings();
    Task<AiModelSettings> SaveDefaultAsync(string provider, string model, string? newKey, CancellationToken token);
    Task<AiModelSettings> RemoveKeyAsync(string provider, CancellationToken token);
    Task TestModelAsync(string provider, string model, string? newKey, CancellationToken token);
    Task TestGatewayAsync(CancellationToken token);
    Task<AiModelAnswer> AskDefaultAsync(string expectedProvider, string expectedModel, string prompt, CancellationToken token);
    Task<AiModelAnswer> AskChatAsync(string expectedProvider, string expectedModel, AiChatRequest request, CancellationToken token);
}
