namespace CRS.AI.Models;

/// <summary>一个预设供应商及可用于公开 ETF 工具调用的模型。</summary>
public sealed record AiProviderPreset(string Id, string DisplayName, IReadOnlyList<string> Models);

/// <summary>设置页读取的供应商状态。此类型只公开模型名称和密钥配置状态。</summary>
public sealed record AiProviderSetting(string Model, bool HasApiKey);

/// <summary>按 Windows 用户保存的模型选择和各供应商配置状态。</summary>
public sealed record AiModelSettings(string? DefaultProvider, IReadOnlyDictionary<string, AiProviderSetting> Providers)
{
    public const int MaxPromptLength = 4000;
    public const int MaxApiKeyLength = 4096;

    public static IReadOnlyList<AiProviderPreset> Presets { get; } = Array.AsReadOnly<AiProviderPreset>(
    [
        new("deepseek", "DeepSeek", Array.AsReadOnly(new[] { "deepseek-flash", "deepseek-v4-pro" })),
        new("openai", "OpenAI", Array.AsReadOnly(new[] { "gpt-5.4-mini", "gpt-5-mini" })),
        new("mimo", "小米 MiMo", Array.AsReadOnly(new[] { "mimo-v2.6-flash", "mimo-v2.6-pro" })),
        new("anthropic", "Claude（Anthropic）", Array.AsReadOnly(new[] { "claude-haiku-5-5", "claude-sonnet-5-5" }))
    ]);

    internal static void ValidateSelection(string provider, string model)
    {
        if (!Presets.Any(preset => preset.Id == provider && preset.Models.Contains(model, StringComparer.Ordinal)))
            throw new AiModelException("InvalidModel");
    }

    /// <summary>依据已适配端点的图像能力决定是否可发送截图。</summary>
    public static bool SupportsScreenshots(string provider, string model) =>
        Presets.Any(item => item.Id == provider && item.Models.Contains(model)) &&
        (provider != "deepseek" || model == "deepseek-flash");

    internal static void ValidateKey(string value)
    {
        // 请求凭证只接受可打印 ASCII，拒绝空白、换行及其他无效请求头字符。
        if (value.Length is 0 or > MaxApiKeyLength || value.Any(c => c is <= ' ' or > '~'))
            throw new AiModelException("InvalidApiKey");
    }
}
