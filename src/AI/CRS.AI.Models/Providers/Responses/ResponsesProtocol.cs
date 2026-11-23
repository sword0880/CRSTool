using System.Text.Json;

namespace CRS.AI.Models.Providers.Responses;

/// <summary>模型发起的函数调用，调用标识用于匹配回传的工具结果。</summary>
internal sealed record ResponsesToolCall(string Id, string? Name, JsonElement Arguments);

/// <summary>从 Responses API 响应中校验并提取工具调用、答案和用量。</summary>
internal static class ResponsesProtocol
{
    /// <summary>只接收已完成的响应，并保留后续请求可回传的输出项。</summary>
    public static JsonElement[] ReadOutput(JsonElement response)
    {
        if (String(response, "status") != "completed")
            throw new AiModelException("IncompleteResponse");
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array ||
            output.GetArrayLength() is < 1 or > 32)
            throw new AiModelException("InvalidResponse");
        var items = output.EnumerateArray().ToArray();
        if (items.Any(item => String(item, "type") is not ("message" or "reasoning" or "function_call")))
            throw new AiModelException("InvalidResponse");
        return items;
    }

    /// <summary>提取一轮中的函数调用，保留调用标识以回传各自结果。</summary>
    public static ResponsesToolCall[] ReadCalls(JsonElement[] output) =>
        output.Where(item => String(item, "type") == "function_call").Select(ReadCall).ToArray();

    private static ResponsesToolCall ReadCall(JsonElement call)
    {
        var id = String(call, "call_id");
        if (!AiModelTurn.IsCallIdValid(id))
            throw new AiModelException("InvalidResponse");

        var raw = String(call, "arguments");
        var arguments = default(JsonElement);
        if (raw is { Length: > 0 and <= 4096 })
        {
            try
            {
                using var document = JsonDocument.Parse(raw,
                    new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
                arguments = document.RootElement.Clone();
            }
            catch (JsonException) { /* 用固定的参数错误回复本轮工具调用。 */ }
        }
        return new(id!, String(call, "name"), arguments);
    }

    /// <summary>提取助手消息中的文本。若存在拒绝项，优先返回拒绝说明。</summary>
    public static (string Text, bool Refused) ReadAnswer(JsonElement[] output)
    {
        var messages = new List<string>();
        var refusals = new List<string>();
        foreach (var item in output)
        {
            if (String(item, "type") != "message" || String(item, "role") != "assistant" ||
                !item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (String(part, "type") == "output_text" && String(part, "text") is { Length: > 0 } text)
                    messages.Add(text);
                if (String(part, "type") == "refusal" && String(part, "refusal") is { Length: > 0 } refusal)
                    refusals.Add(refusal);
            }
        }
        if (refusals.Count > 0) return (string.Join(Environment.NewLine, refusals), true);
        if (messages.Count == 0) throw new AiModelException("MissingAnswer");
        return (string.Join(Environment.NewLine, messages), false);
    }

    /// <summary>读取非负整数用量。回执缺少用量或字段无效时终止本次查询。</summary>
    public static AiTokenUsage ReadUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new AiModelException("InvalidUsage");
        return new(TokenCount(usage, "input_tokens"), TokenCount(usage, "output_tokens"),
            TokenCount(usage, "total_tokens"));
    }

    private static int TokenCount(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var count) && count >= 0 ? count : throw new AiModelException("InvalidUsage");

    private static string? String(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
