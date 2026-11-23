using System.Net.Http;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Gateway.Transport;
using CRS.AI.Models.Configuration;
using CRS.AI.Models.Providers;
using CRS.AI.Models.Providers.Anthropic;
using CRS.AI.Models.Providers.DeepSeek;
using CRS.AI.Models.Providers.MiMo;
using CRS.AI.Models.Providers.Responses;
using CRS.AI.Models.Tools;

namespace CRS.AI.Models;

/// <summary>连接供应商、凭证存储和 Gateway。模型测试与本机工具测试各自独立。</summary>
public sealed class AiModelService : IAiModelService
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });

    private readonly AiModelSettingsStore store;
    private readonly HttpClient http;
    private readonly AiToolDispatch dispatch;

    public AiModelService(string? localDataRoot = null)
        : this(localDataRoot, SharedHttp, new(new WindowsAiReadClient().ExecuteAsync)) { }

    internal AiModelService(string? localDataRoot, HttpClient http, AiToolDispatch dispatch)
    {
        store = new(localDataRoot);
        this.http = http;
        this.dispatch = dispatch;
    }

    public AiModelSettings LoadSettings() => store.Load();

    public Task<AiModelSettings> SaveDefaultAsync(string provider, string model, string? newKey, CancellationToken token) =>
        Task.Run(() => store.Save(provider, model, newKey, token), token);

    public Task<AiModelSettings> RemoveKeyAsync(string provider, CancellationToken token) =>
        Task.Run(() => store.Remove(provider, token), token);

    public Task TestModelAsync(string provider, string model, string? newKey, CancellationToken token) =>
        RunModelAsync(async () =>
        {
            AiModelSettings.ValidateSelection(provider, model);
            var key = newKey ?? store.ReadKey(provider);
            AiModelSettings.ValidateKey(key);
            await CreateProvider(provider, model, key).TestConnectionAsync(token).ConfigureAwait(false);
            return true;
        }, token);

    public async Task TestGatewayAsync(CancellationToken token)
    {
        // 只做本机查询，调用结果用于判断可用性，不发送给模型供应商。
        var result = await dispatch.ExecuteAsync(AiCatalog.Overview, AiToolDispatch.EmptyArguments, token).ConfigureAwait(false);
        if (result.Status == "Rejected" || result.Data is null)
            throw new AiModelException(result.Code ?? "GatewayUnavailable");
    }

    public Task<AiModelAnswer> AskDefaultAsync(string expectedProvider, string expectedModel, string prompt, CancellationToken token) =>
        AskChatAsync(expectedProvider, expectedModel, new(prompt, []), token);

    public Task<AiModelAnswer> AskChatAsync(string expectedProvider, string expectedModel, AiChatRequest request, CancellationToken token) =>
        RunModelAsync(() =>
        {
            var (provider, model, key) = store.ReadDefault();
            // 如果另一窗口修改了默认模型，先更新界面再允许发送，避免问题发往不同供应商。
            if (provider != expectedProvider || model != expectedModel)
                throw new AiModelException("DefaultModelChanged");
            if (request.Screenshot is not null && !AiModelSettings.SupportsScreenshots(provider, model))
                throw new AiModelException("ImageUnsupported");
            // 小米的 Messages 兼容接口有明确的 Base64 图片契约。
            var client = provider == "mimo" && request.Screenshot is not null
                ? new AnthropicMessagesClient(http, key, model, dispatch, mimo: true)
                : CreateProvider(provider, model, key);
            return client.AskChatAsync(request, token);
        }, token);

    // 供应商与固定官方地址绑定，用户无需填写协议或服务器地址。
    private IAiModelProvider CreateProvider(string provider, string model, string key) => provider switch
    {
        "openai" => new ResponsesApiClient(http, key, model, dispatch),
        "deepseek" => new ResponsesApiClient(http, key, model, dispatch, new DeepSeekResponsesOptions()),
        "mimo" => new ResponsesApiClient(http, key, model, dispatch, new MiMoResponsesOptions()),
        "anthropic" => new AnthropicMessagesClient(http, key, model, dispatch),
        _ => throw new AiModelException("InvalidModel")
    };

    private static Task<T> RunModelAsync<T>(Func<Task<T>> run, CancellationToken token) =>
        Task.Run(async () =>
        {
            try { return await run().ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new AiModelException("ApiTimeout"); }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException)
            { throw new AiModelException("ApiConnectionFailed"); }
        }, token);
}
