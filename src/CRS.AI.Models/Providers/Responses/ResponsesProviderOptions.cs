namespace CRS.AI.Models.Providers.Responses;

/// <summary>记录供应商各自支持的 Responses 字段及本轮允许的调用数量。</summary>
internal abstract record ResponsesProviderOptions(Uri Endpoint, bool Strict, int MaxCallsPerRound, int ProbeMaxTokens)
{
    public abstract void Configure(Dictionary<string, object> body, string model, bool probe);
}

/// <summary>保留 OpenAI 既有的不存储、串行工具及加密推理回传约定。</summary>
internal sealed record OpenAiResponsesOptions() :
    ResponsesProviderOptions(new Uri("https://api.openai.com/v1/responses"), true, 1, 64)
{
    public override void Configure(Dictionary<string, object> body, string model, bool probe)
    {
        body["store"] = false;
        body["parallel_tool_calls"] = false;
        // 两个 mini 型号的最低推理档位不同，连接测试使用各自支持的值。
        if (probe) body["reasoning"] = new { effort = model == "gpt-5-mini" ? "minimal" : "none" };
        else body["include"] = new[] { "reasoning.encrypted_content" };
    }
}
