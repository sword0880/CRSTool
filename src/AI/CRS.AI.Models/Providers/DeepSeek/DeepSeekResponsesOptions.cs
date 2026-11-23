using CRS.AI.Models.Providers.Responses;

namespace CRS.AI.Models.Providers.DeepSeek;

/// <summary>使用 DeepSeek 官方 Responses 端点，按其支持范围构造请求。</summary>
internal sealed record DeepSeekResponsesOptions() :
    ResponsesProviderOptions(new Uri("https://api.deepseek.com/responses"), false, 3, 32)
{
    public override void Configure(Dictionary<string, object> body, string model, bool probe)
    {
        // DeepSeek 始终允许并行函数调用。客户端按顺序执行并在本地执行总量限制。
        // 本版使用非思考模式，避免把明文推理当作答案或依赖 OpenAI 的加密推理字段。
        body["reasoning"] = new { effort = "none" };
    }
}
