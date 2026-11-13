using System.Net;
using System.Text.Json;
using CRS.AI.Models;
using CRS.AI.Models.Providers.Responses;
using CRS.AI.Models.Tools;
using CRS.AI.Contracts;

namespace CRS.Tests.AiQuery.Checks;

/// <summary>用合成 HTTP 回执验收多轮工具调用和异常处理，测试不依赖 API 凭证。</summary>
internal static class ResponsesApiChecks
{
    private const string FunctionResponse = """
        {"status":"completed","output":[
          {"type":"reasoning","id":"rs_sample","summary":[],"encrypted_content":"synthetic"},
          {"type":"function_call","id":"fc_sample","call_id":"call_1","name":"crs_get_etf_overview_v1","arguments":"{}","status":"completed"}
        ],"usage":{"input_tokens":40,"output_tokens":10,"total_tokens":50}}
        """;
    private const string AnswerResponse = """
        {"status":"completed","output":[
          {"type":"message","role":"assistant","content":[{"type":"output_text","text":"公开 ETF 样本。"}]}
        ],"usage":{"input_tokens":50,"output_tokens":8,"total_tokens":58}}
        """;

    /// <summary>覆盖成功回传，以及未知工具、非法参数、重复调用、限流和用量超限。</summary>
    public static async Task RunAsync()
    {
        var calls = 0;
        var dispatch = new AiToolDispatch((call, _) =>
        {
            calls++;
            Check(call.Tool == AiCatalog.Overview && call.Arguments.GetRawText() == "{}", "只转发工具目录中的 JSON 参数");
            return Task.FromResult(new AiResult("1.0", AiCatalog.Overview, "Partial", "FinancialAcceptanceNotEstablished",
                DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { items = Array.Empty<int>() }), [])
                { DataVersion = "sample-version" });
        });
        var answer = await RunToolRoundAsync(dispatch, CancellationToken.None);
        Check(answer is { Text: "公开 ETF 样本。", Refused: false, Requests: 2, ToolCalls: 1 } &&
              answer.Usage == new AiTokenUsage(90, 18, 108) && calls == 1, "两轮 Responses 调用及用量归集");

        using var invalidTools = new HttpClient(new ScriptedHandler((round, body) =>
        {
            if (round == 1)
                return JsonReply(FunctionResponse.Replace(AiCatalog.Overview, "private_tool", StringComparison.Ordinal));
            if (round == 2)
            {
                Check(ReadToolResult(body).GetProperty("code").GetString() == "UnknownTool", "未知工具回复固定错误码");
                return JsonReply(FunctionResponse.Replace("call_1", "call_2", StringComparison.Ordinal)
                    .Replace("\"arguments\":\"{}\"", "\"arguments\":\"[]\"", StringComparison.Ordinal));
            }
            Check(ReadToolResult(body).GetProperty("code").GetString() == "InvalidArguments", "畸形参数回复固定错误码");
            return JsonReply(AnswerResponse);
        }));
        await new ResponsesApiClient(invalidTools, "synthetic-key", "synthetic-model", dispatch)
            .AskAsync("公开 ETF 概览", CancellationToken.None);
        Check(calls == 1, "未知工具和无效参数均无法进入 Windows Gateway");

        using var repeated = new HttpClient(new ScriptedHandler((_, _) => JsonReply(FunctionResponse)));
        await ExpectAsync(repeated, dispatch, "RepeatedToolCall");
        Check(calls == 2, "重复调用标识不会再次执行工具");
        using var limited = new HttpClient(new ScriptedHandler((round, _) =>
        {
            Check(round == 1, "服务限流时只发送一次请求");
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        }));
        await ExpectAsync(limited, dispatch, "ApiRateLimited");
        using var overBudget = new HttpClient(new ScriptedHandler((_, _) =>
            JsonReply(FunctionResponse.Replace("\"total_tokens\":50", "\"total_tokens\":24000", StringComparison.Ordinal))));
        await ExpectAsync(overBudget, dispatch, "TurnBudgetExceeded");
        Check(calls == 2, "累计用量到达阈值后停止工具调用");
    }

    /// <summary>首轮返回函数调用，下一轮校验回传内容后返回答案。隔离验收可传入真实管道分发器。</summary>
    public static async Task<AiModelAnswer> RunToolRoundAsync(AiToolDispatch dispatch, CancellationToken cancellation)
    {
        using var http = new HttpClient(new ScriptedHandler((round, body) =>
        {
            Check(!body.GetProperty("store").GetBoolean() && !body.GetProperty("parallel_tool_calls").GetBoolean() &&
                  body.GetProperty("model").GetString() == "synthetic-model" &&
                  body.GetProperty("include")[0].GetString() == "reasoning.encrypted_content", "按 Responses 协议发送请求");
            Check(body.GetProperty("tools").EnumerateArray().Select(tool =>
                    (tool.GetProperty("name").GetString(), tool.GetProperty("strict").GetBoolean()))
                .SequenceEqual(AiCatalog.Tools.Select(tool => ((string?)tool.Name, true))), "函数定义与共享目录一致");
            if (round == 1)
                return JsonReply(FunctionResponse);
            var input = body.GetProperty("input");
            Check(round == 2 && input.GetArrayLength() == 4 &&
                  input[1].GetProperty("encrypted_content").GetString() == "synthetic" &&
                  input[2].GetProperty("call_id").GetString() == "call_1" &&
                  input[3].GetProperty("call_id").GetString() == "call_1" &&
                  ReadToolResult(body).GetProperty("status").GetString() == "Partial" &&
                  ReadToolResult(body).GetProperty("dataVersion").GetString() is { Length: > 0 },
                "回传原始加密推理项、对应工具结果及版本");
            return JsonReply(AnswerResponse);
        }));
        return await new ResponsesApiClient(http, "synthetic-key", "synthetic-model", dispatch)
            .AskAsync("公开 ETF 概览", cancellation);
    }

    private static async Task ExpectAsync(HttpClient http, AiToolDispatch dispatch, string code)
    {
        try
        {
            await new ResponsesApiClient(http, "synthetic-key", "synthetic-model", dispatch)
                .AskAsync("公开 ETF 概览", CancellationToken.None);
        }
        catch (AiModelException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("未拦截 " + code);
    }

    private static JsonElement ReadToolResult(JsonElement body)
    {
        var last = body.GetProperty("input").EnumerateArray().Last();
        Check(last.GetProperty("type").GetString() == "function_call_output", "按 Responses 协议提交工具结果");
        using var result = JsonDocument.Parse(last.GetProperty("output").GetString()!);
        return result.RootElement.Clone();
    }

    private static HttpResponseMessage JsonReply(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS Responses " + label);
    }

    /// <summary>截取客户端的真实 HTTP 请求，按轮次校验请求并返回合成响应。</summary>
    private sealed class ScriptedHandler(Func<int, JsonElement, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri == new Uri("https://api.openai.com/v1/responses") &&
                  request.Headers.Authorization is { Scheme: "Bearer", Parameter: "synthetic-key" },
                "使用 OpenAI 官方端点及项目凭证");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellation));
            return respond(++requests, body.RootElement);
        }
    }
}
