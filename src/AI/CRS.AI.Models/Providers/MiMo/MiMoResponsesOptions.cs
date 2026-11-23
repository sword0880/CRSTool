using CRS.AI.Models.Providers.Responses;

namespace CRS.AI.Models.Providers.MiMo;

/// <summary>小米 MiMo 官方 Responses 兼容选项。</summary>
internal sealed record MiMoResponsesOptions() :
    ResponsesProviderOptions(new Uri("https://api.xiaomimimo.com/v1/responses"), false, 3, 32)
{
    public override void Configure(Dictionary<string, object> body, string model, bool probe)
    {
        body["reasoning"] = new { effort = "none" };
        // MiMo 会忽略非 auto 的 tool_choice，收尾时移除工具定义以避免追加调用。
        if (body.TryGetValue("tool_choice", out var choice) && Equals(choice, "none"))
        {
            body.Remove("tool_choice");
            body.Remove("tools");
        }
    }
}
