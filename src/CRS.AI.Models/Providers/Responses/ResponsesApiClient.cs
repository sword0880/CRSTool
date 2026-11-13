using System.Text.Json;
using CRS.AI.Adapters;
using CRS.AI.Models.Tools;
using CRS.AI.Contracts;

namespace CRS.AI.Models.Providers.Responses;

/// <summary>按供应商的 Responses 约定发送请求，通过本机分发器执行模型请求的只读工具。</summary>
internal sealed class ResponsesApiClient(HttpClient http, string apiKey, string model, AiToolDispatch dispatch,
    ResponsesProviderOptions? options = null) : IAiModelProvider
{
    private readonly ResponsesProviderOptions provider = options ?? new OpenAiResponsesOptions();
    private static readonly IReadOnlyList<OpenAiFunctionDefinition> Tools =
        OpenAiFunctionReadAdapter.DescribeTools(AiCatalog.Tools);

    /// <summary>在统一时限内完成一条问题，汇总每轮用量并返回最终答案。</summary>
    public Task<AiModelAnswer> AskAsync(string prompt, CancellationToken token) =>
        AskChatAsync(new(prompt, []), token);

    public async Task<AiModelAnswer> AskChatAsync(AiChatRequest request, CancellationToken token)
    {
        AiModelTurn.Validate(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var input = new List<object>();
        foreach (var entry in request.History)
        {
            input.Add(new { role = "user", content = entry.Question });
            input.Add(new { role = "assistant", content = entry.Answer });
        }
        // 图片只附在当前用户消息，使用内嵌 JPEG，不创建外部文件地址。
        object content = request.Screenshot is { } image ? new object[]
        {
            new { type = "input_text", text = request.Prompt },
            new { type = "input_image", image_url = "data:image/jpeg;base64," + Convert.ToBase64String(image) }
        } : request.Prompt;
        input.Add(new { role = "user", content });
        var turn = new AiModelTurn();
        for (var round = 1; round <= AiModelTurn.MaxRequests; round++)
        {
            var allowTool = turn.CanUseTools(round);
            using var response = await SendAsync(input, allowTool, probe: false, deadline.Token,
                includesScreenshot: request.Screenshot is not null);
            var root = response.RootElement;
            var output = ResponsesProtocol.ReadOutput(root);
            turn.AddUsage(ResponsesProtocol.ReadUsage(root));
            var calls = ResponsesProtocol.ReadCalls(output);
            if (calls.Length == 0)
            {
                var answer = ResponsesProtocol.ReadAnswer(output);
                return new(answer.Text, answer.Refused, turn.Usage, round, turn.ToolCalls);
            }
            turn.AdmitTools(calls.Select(call => call.Id).ToArray(), round, provider.MaxCallsPerRound);

            // 每轮回传模型输出与对应工具结果，保持完整对话上下文。
            input.AddRange(output.Select(item => (object)item.Clone()));
            foreach (var call in calls)
            {
                var result = await dispatch.ExecuteAsync(call.Name, call.Arguments, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                input.Add(new { type = "function_call_output", call_id = call.Id,
                    output = JsonSerializer.Serialize(result, AiModelHttp.Json) });
            }
        }
        throw new AiModelException("TurnBudgetExceeded");
    }

    /// <summary>只发送固定短提示，验证模型可返回答案，无需访问 Gateway。</summary>
    public async Task TestConnectionAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await SendAsync([new { role = "user", content = "只回复 OK" }], false, true, deadline.Token);
        var output = ResponsesProtocol.ReadOutput(response.RootElement);
        if (ResponsesProtocol.ReadCalls(output).Length != 0 || ResponsesProtocol.ReadAnswer(output).Refused)
            throw new AiModelException("InvalidProbe");
    }

    /// <summary>发送单轮请求，限制双向报文大小，并将上游失败转换为固定错误码。</summary>
    private Task<JsonDocument> SendAsync(List<object> input, bool allowTool, bool probe, CancellationToken token,
        bool includesScreenshot = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = input,
            ["max_output_tokens"] = probe ? provider.ProbeMaxTokens : 1024
        };
        if (!probe)
        {
            payload["instructions"] = AiModelTurn.Instructions;
            payload["tools"] = provider.Strict ? Tools : Tools.Select(tool => tool with { Strict = false }).ToArray();
            payload["tool_choice"] = allowTool ? "auto" : "none";
        }
        provider.Configure(payload, model, probe);
        return AiModelHttp.PostAsync(http, provider.Endpoint, apiKey, payload, token, includesScreenshot: includesScreenshot);
    }
}
