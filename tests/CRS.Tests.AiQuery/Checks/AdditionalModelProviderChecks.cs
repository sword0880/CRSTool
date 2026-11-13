using System.Net;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Models;
using CRS.AI.Models.Providers.Anthropic;
using CRS.AI.Models.Providers.MiMo;
using CRS.AI.Models.Providers.Responses;
using CRS.AI.Models.Tools;

namespace CRS.Tests.AiQuery.Checks;

/// <summary>以模拟回执核对 MiMo 和 Claude 的端点、工具历史和收尾约束。</summary>
internal static class AdditionalModelProviderChecks
{
    public static async Task RunAsync()
    {
        var calls = new List<string>();
        var dispatch = new AiToolDispatch((call, _) =>
        {
            calls.Add(call.Tool);
            return Task.FromResult(new AiResult("1.0", call.Tool, "Partial", null,
                DateTimeOffset.UtcNow, AiToolDispatch.EmptyArguments, []) { DataVersion = "sample-version" });
        });
        using var mimoHttp = new HttpClient(new ScriptedHandler(CheckMiMo));
        var mimo = new ResponsesApiClient(mimoHttp, "synthetic-mimo", "mimo-v2.6-flash", dispatch, new MiMoResponsesOptions());
        await mimo.TestConnectionAsync(default);
        Check(calls.Count == 0, "MiMo 连接测试不执行工具");
        var mimoAnswer = await mimo.AskAsync("公开 ETF 概览", default);
        Check(mimoAnswer is { Text: "MiMo 样本", ToolCalls: 3, Requests: 2 } && calls.Count == 3,
            "MiMo 执行有界工具后得到回答");

        calls.Clear();
        using var claudeHttp = new HttpClient(new ScriptedHandler(CheckClaude));
        var claude = new AnthropicMessagesClient(claudeHttp, "synthetic-claude", "claude-haiku-5-5", dispatch);
        await claude.TestConnectionAsync(default);
        Check(calls.Count == 0, "Claude 连接测试不执行工具");
        var claudeAnswer = await claude.AskAsync("公开 ETF 概览", default);
        Check(claudeAnswer is { Text: "Claude 样本", ToolCalls: 2, Requests: 2 } &&
              claudeAnswer.Usage == new AiTokenUsage(52, 12, 64) && calls.SequenceEqual([AiCatalog.Overview]),
            "Claude 归集缓存用量并拦截目录外工具");
        await CheckScreenshotAsync(dispatch, useMessages: false);
        await CheckScreenshotAsync(dispatch, useMessages: true);
    }

    private static async Task CheckScreenshotAsync(AiToolDispatch dispatch, bool useMessages)
    {
        // 图片只应出现在当前用户消息，历史只传回文字。
        using var http = new HttpClient(new ScriptedHandler((_, request, payload) =>
        {
            var messages = payload.GetProperty(useMessages ? "messages" : "input");
            var current = messages[2].GetProperty("content");
            Check(messages.GetArrayLength() == 3 && messages[0].GetProperty("content").GetString() == "上一条问题" &&
                  messages[1].GetProperty("content").GetString() == "上一条回答", "图片请求保留近期文字上下文");
            if (useMessages)
            {
                Check(request.RequestUri == new Uri("https://api.xiaomimimo.com/anthropic/v1/messages") &&
                      request.Headers.Authorization?.Parameter == "synthetic" &&
                      current[1].GetProperty("source").GetProperty("media_type").GetString() == "image/jpeg",
                    "MiMo 截图使用官方 Messages 端点和内嵌 Base64");
                return """
                    {"type":"message","role":"assistant","stop_reason":"end_turn",
                    "content":[{"type":"text","text":"截图样本"}],"usage":{"input_tokens":20,"output_tokens":4}}
                    """;
            }
            Check(current[1].GetProperty("type").GetString() == "input_image" &&
                  current[1].GetProperty("image_url").GetString()!.StartsWith("data:image/jpeg;base64,"),
                "Responses 截图仅包含当前选择的内嵌图像");
            return """
                {"status":"completed","output":[{"type":"message","role":"assistant",
                "content":[{"type":"output_text","text":"截图样本"}]}],
                "usage":{"input_tokens":20,"output_tokens":4,"total_tokens":24}}
                """;
        }));
        var input = new AiChatRequest("看截图", [new("上一条问题", "上一条回答")], [0xff, 0xd8, 0xff, 0xd9]);
        var result = useMessages
            ? await new AnthropicMessagesClient(http, "synthetic", "mimo-v2.6-flash", dispatch, mimo: true).AskChatAsync(input, default)
            : await new ResponsesApiClient(http, "synthetic", "gpt-5.4-mini", dispatch).AskChatAsync(input, default);
        Check(result.Text == "截图样本", "截图请求的回答正常返回");
    }

    private static string CheckMiMo(int round, HttpRequestMessage request, JsonElement payload)
    {
        Check(request.RequestUri == new Uri("https://api.xiaomimimo.com/v1/responses") &&
              request.Headers.Authorization?.Parameter == "synthetic-mimo" &&
              payload.GetProperty("model").GetString() == "mimo-v2.6-flash",
            "MiMo 使用官方 Responses 端点和本供应商凭证");
        var input = payload.GetProperty("input");
        Check(payload.GetProperty("reasoning").GetProperty("effort").GetString() == "none", "MiMo 使用非思考模式");
        if (round == 2)
        {
            Check(payload.GetProperty("instructions").GetString()!.StartsWith("你是小财牛") &&
                  !payload.GetProperty("tools")[0].GetProperty("strict").GetBoolean() &&
                  payload.GetProperty("tool_choice").GetString() == "auto", "MiMo 获取小财牛提示和公开工具");
            return """
                {"status":"completed","output":[
                  {"type":"function_call","call_id":"call_1","name":"crs_get_etf_overview_v1","arguments":"{}"},
                  {"type":"function_call","call_id":"call_2","name":"crs_get_etf_overview_v1","arguments":"{}"},
                  {"type":"function_call","call_id":"call_3","name":"crs_get_etf_overview_v1","arguments":"{}"}
                ],"usage":{"input_tokens":20,"output_tokens":8,"total_tokens":28}}
                """;
        }
        Check(!payload.TryGetProperty("tools", out _) && !payload.TryGetProperty("tool_choice", out _),
            "MiMo 连接测试或预算耗尽时省略工具定义");
        if (round == 3)
            Check(input.GetArrayLength() == 7 && input[6].GetProperty("call_id").GetString() == "call_3" &&
                  input[6].GetProperty("output").GetString()!.Contains("sample-version"), "MiMo 保留工具结果与调用标识");
        return """
            {"status":"completed","output":[{"type":"message","role":"assistant",
            "content":[{"type":"output_text","text":"MiMo 样本"}]}],
            "usage":{"input_tokens":20,"output_tokens":4,"total_tokens":24}}
            """;
    }

    private static string CheckClaude(int round, HttpRequestMessage request, JsonElement payload)
    {
        Check(request.RequestUri == new Uri("https://api.anthropic.com/v1/messages") &&
              request.Headers.GetValues("x-api-key").Single() == "synthetic-claude" &&
              request.Headers.GetValues("anthropic-version").Single() == "2023-06-01" &&
              request.Headers.Authorization is null && payload.GetProperty("model").GetString() == "claude-haiku-5-5",
            "Claude 使用官方 Messages 端点、版本头及专用凭证");
        var messages = payload.GetProperty("messages");
        if (round == 1)
            Check(!payload.TryGetProperty("tools", out _) &&
                  messages[0].GetProperty("content").GetString() == "只回复 OK", "Claude 模型测试仅发送固定短提示");
        if (round == 2)
        {
            Check(payload.GetProperty("system").GetString()!.StartsWith("你是小财牛") &&
                  payload.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("type").GetString() == "object",
                "Claude 获取小财牛提示和 Messages 工具结构");
            return """
                {"type":"message","role":"assistant","stop_reason":"tool_use","content":[
                  {"type":"thinking","thinking":"synthetic","signature":"sample-signature"},
                  {"type":"tool_use","id":"toolu_1","name":"crs_get_etf_overview_v1","input":{}},
                  {"type":"tool_use","id":"toolu_2","name":"private_tool","input":{}}
                ],"usage":{"input_tokens":20,"cache_read_input_tokens":5,"output_tokens":8}}
                """;
        }
        if (round == 3)
        {
            var results = messages[2].GetProperty("content");
            Check(messages[1].GetProperty("content")[0].GetProperty("signature").GetString() == "sample-signature" &&
                  messages[2].GetProperty("role").GetString() == "user" && results.GetArrayLength() == 2 &&
                  results[0].GetProperty("tool_use_id").GetString() == "toolu_1" &&
                  !results[0].GetProperty("is_error").GetBoolean() &&
                  results[1].GetProperty("tool_use_id").GetString() == "toolu_2" &&
                  results[1].GetProperty("is_error").GetBoolean(), "Claude 原样回传助手内容并成组提交工具结果");
        }
        return """
            {"type":"message","role":"assistant","stop_reason":"end_turn",
            "content":[{"type":"text","text":"Claude 样本"}],
            "usage":{"input_tokens":20,"cache_creation_input_tokens":7,"output_tokens":4}}
            """;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, JsonElement, string> respond) : HttpMessageHandler
    {
        private int round;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(respond(++round, request, body.RootElement)) };
        }
    }
}
