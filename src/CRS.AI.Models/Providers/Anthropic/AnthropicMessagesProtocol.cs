using System.Text.Json;

namespace CRS.AI.Models.Providers.Anthropic;

/// <summary>解析 Claude 的 Messages 回复，保留可原样回传的助手内容块。</summary>
internal sealed record AnthropicMessage(string StopReason, JsonElement Content, AiTokenUsage Usage)
{
    public static AnthropicMessage Read(JsonElement response)
    {
        if (ReadString(response, "type") != "message" || ReadString(response, "role") != "assistant" ||
            !response.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array ||
            content.GetArrayLength() > 32 || ReadString(response, "stop_reason") is not { } stop ||
            !response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new AiModelException("InvalidResponse");
        if (stop is not ("end_turn" or "tool_use" or "refusal"))
            throw new AiModelException("IncompleteResponse");
        if (content.EnumerateArray().Any(block => ReadString(block, "type") is not
            ("text" or "thinking" or "redacted_thinking" or "tool_use")))
            throw new AiModelException("InvalidResponse");
        // Claude 将缓存输入单独计数，额度判断仍需包含这些输入。
        var input = TokenCount(usage, "input_tokens") +
            TokenCount(usage, "cache_creation_input_tokens", optional: true) +
            TokenCount(usage, "cache_read_input_tokens", optional: true);
        var output = TokenCount(usage, "output_tokens");
        return new(stop, content, new(input, output, input + output));
    }

    public AnthropicToolCall[] ReadCalls()
    {
        var calls = Content.EnumerateArray().Where(block => ReadString(block, "type") == "tool_use")
            .Select(ReadCall).ToArray();
        if ((StopReason == "tool_use") != (calls.Length > 0))
            throw new AiModelException("UnexpectedToolCall");
        return calls;
    }

    public (string Text, bool Refused) ReadAnswer()
    {
        var parts = Content.EnumerateArray().Where(block => ReadString(block, "type") == "text")
            .Select(block => ReadString(block, "text")).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (parts.Length > 0) return (string.Join(Environment.NewLine, parts), StopReason == "refusal");
        if (StopReason == "refusal") return ("Claude 拒绝了本次请求。", true);
        throw new AiModelException("MissingAnswer");
    }

    private static AnthropicToolCall ReadCall(JsonElement block)
    {
        var id = ReadString(block, "id");
        if (!AiModelTurn.IsCallIdValid(id)) throw new AiModelException("InvalidResponse");
        block.TryGetProperty("input", out var arguments);
        return new(id!, ReadString(block, "name"), arguments);
    }

    private static long TokenCount(JsonElement usage, string name, bool optional = false)
    {
        if (!usage.TryGetProperty(name, out var value) && optional) return 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0
            ? count : throw new AiModelException("InvalidUsage");
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

internal sealed record AnthropicToolCall(string Id, string? Name, JsonElement Arguments);
