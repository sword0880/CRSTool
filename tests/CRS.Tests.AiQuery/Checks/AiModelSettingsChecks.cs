using System.Net;
using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Models;
using CRS.AI.Models.Tools;

namespace CRS.Tests.AiQuery.Checks;

/// <summary>使用临时目录、真实 DPAPI 与模拟模型回执验证供应商隔离和默认模型切换。</summary>
internal static class AiModelSettingsChecks
{
    private const string DeepSeekKey = "synthetic-deepseek-key";
    private const string OpenAiKey = "synthetic-openai-key";
    private const string Usage = """ "usage":{"input_tokens":20,"output_tokens":4,"total_tokens":24} """;

    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "CRS.AiModels." + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "CRS", "AI", "models.json");
        var calls = new List<string>();
        var requests = new List<string>();
        using var http = new HttpClient(new ScriptedModels(requests));
        var dispatch = new AiToolDispatch((call, _) =>
        {
            calls.Add(call.Tool);
            return Task.FromResult(new AiResult("1.0", call.Tool, "Partial", null,
                DateTimeOffset.UtcNow, AiToolDispatch.EmptyArguments, []) { DataVersion = "sample-version" });
        });
        var service = new AiModelService(root, http, dispatch);
        try
        {
            Check(service.LoadSettings().DefaultProvider is null, "首次启动没有默认供应商");
            await service.TestModelAsync("deepseek", "deepseek-flash", DeepSeekKey, default);
            Check(calls.Count == 0 && !File.Exists(path), "模型测试独立执行，不调用 Gateway 或保存密钥");
            await service.TestGatewayAsync(default);
            Check(calls.SequenceEqual([AiCatalog.Overview]) && requests.SequenceEqual(["deepseek-flash"]), "本机测试不发起外部请求");
            calls.Clear();

            await service.SaveDefaultAsync("deepseek", "deepseek-flash", DeepSeekKey, default);
            await service.SaveDefaultAsync("openai", "gpt-5.4-mini", OpenAiKey, default);
            var reopened = new AiModelService(root, http, dispatch);
            var saved = reopened.LoadSettings();
            var file = await File.ReadAllTextAsync(path);
            Check(saved.DefaultProvider == "openai" && saved.Providers.Count == 2 &&
                  !file.Contains(DeepSeekKey, StringComparison.Ordinal) && !file.Contains(OpenAiKey, StringComparison.Ordinal),
                "默认模型和两个密钥可重新加载，文件无明文凭证");
            await reopened.TestModelAsync("openai", "gpt-5.4-mini", null, default);
            var openAiAnswer = await reopened.AskDefaultAsync("openai", "gpt-5.4-mini", "概览", default);
            Check(openAiAnswer is { ToolCalls: 0, Text: "OK" }, "问答使用已保存的 OpenAI 默认模型");

            await reopened.SaveDefaultAsync("deepseek", "deepseek-v4-pro", null, default);
            var previousCount = requests.Count;
            try
            {
                await reopened.AskDefaultAsync("openai", "gpt-5.4-mini", "旧窗口的请求", default);
                throw new InvalidOperationException("已切换供应商仍发送旧窗口请求。");
            }
            catch (AiModelException error) when (error.Code == "DefaultModelChanged")
            { Check(requests.Count == previousCount, "已变更的默认供应商不会收到旧窗口的问题"); }

            var deepSeekAnswer = await reopened.AskDefaultAsync("deepseek", "deepseek-v4-pro", "公开 ETF 查询", default);
            Check(deepSeekAnswer is { ToolCalls: 2, Requests: 2 } &&
                  calls.SequenceEqual([AiCatalog.Overview, AiCatalog.Coverage]),
                "DeepSeek 可复用已存密钥，并按顺序执行同轮工具调用");

            saved = await reopened.RemoveKeyAsync("deepseek", default);
            Check(saved.DefaultProvider is null && saved.Providers.ContainsKey("openai"),
                "移除默认供应商密钥后清空默认值，保留另一供应商配置");
            try { await reopened.AskDefaultAsync("deepseek", "deepseek-v4-pro", "概览", default); }
            catch (AiModelException error) when (error.Code == "DefaultModelMissing")
            {
                Check(requests.Last() == "deepseek-v4-pro", "缺少默认配置时不自动切换供应商");
                return;
            }
            throw new InvalidOperationException("缺少默认配置时仍发起请求。");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
            if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!);
            if (Directory.Exists(Path.Combine(root, "CRS"))) Directory.Delete(Path.Combine(root, "CRS"));
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS AI 模型 " + label);
    }

    /// <summary>核对供应商专用端点、密钥及报文，模拟 DeepSeek 一次请求两项工具。</summary>
    private sealed class ScriptedModels(List<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var payload = body.RootElement;
            var model = payload.GetProperty("model").GetString()!;
            requests.Add(model);
            var deepSeek = model.StartsWith("deepseek-", StringComparison.Ordinal);
            var target = deepSeek ? "https://api.deepseek.com/responses" : "https://api.openai.com/v1/responses";
            Check(request.RequestUri == new Uri(target) &&
                  request.Headers.Authorization?.Parameter == (deepSeek ? DeepSeekKey : OpenAiKey),
                "供应商密钥仅发往对应官方地址");

            var input = payload.GetProperty("input");
            var hasTools = payload.TryGetProperty("tools", out var tools);
            if (!hasTools)
            {
                Check(input[0].GetProperty("content").GetString() == "只回复 OK", "模型连接测试只发送固定提示");
                if (!deepSeek)
                    Check(payload.GetProperty("reasoning").GetProperty("effort").GetString() ==
                          (model == "gpt-5-mini" ? "minimal" : "none"), "OpenAI 连接测试使用对应模型的最低推理档位");
            }
            if (deepSeek)
            {
                Check(!payload.TryGetProperty("include", out _) &&
                      payload.GetProperty("reasoning").GetProperty("effort").GetString() == "none",
                    "DeepSeek 采用支持的非思考请求格式");
                if (hasTools)
                {
                    Check(!tools[0].GetProperty("strict").GetBoolean(), "DeepSeek 使用函数定义并由 Gateway 校验参数");
                    if (input.GetArrayLength() == 1)
                        return Reply("""
                            {"type":"function_call","call_id":"call_one","name":"crs_get_etf_overview_v1","arguments":"{}"},
                            {"type":"function_call","call_id":"call_two","name":"crs_get_etf_coverage_v1",
                            "arguments":"{\"market\":\"SSE\",\"ticker\":\"510300\",\"from\":\"2026-11-02\",\"to\":\"2026-11-02\"}"}
                            """);
                    Check(input.GetArrayLength() == 5 && input[3].GetProperty("call_id").GetString() == "call_one" &&
                          input[4].GetProperty("call_id").GetString() == "call_two", "两项工具结果关联各自调用标识");
                }
            }
            return Reply(""" {"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]} """);
        }

        private static HttpResponseMessage Reply(string output) =>
            new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"completed\",\"output\":[" + output + "]," + Usage + "}") };
    }
}
