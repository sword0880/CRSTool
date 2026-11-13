using System.Text.Json;
using CRS.AI.Contracts;
using CRS.AI.Models.Tools;

namespace CRS.AI.Models.Providers.Anthropic;

/// <summary>通过 Anthropic 官方 Messages API 调用 Claude，工具仍由本机 Gateway 执行。</summary>
internal sealed class AnthropicMessagesClient(HttpClient http, string key, string model, AiToolDispatch dispatch)
    : IAiModelProvider
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/v1/messages");
    private static readonly object[] Tools = AiCatalog.Tools.Select(tool =>
        new { name = tool.Name, description = tool.Description, input_schema = tool.Schema }).ToArray();

    public async Task<AiModelAnswer> AskAsync(string prompt, CancellationToken token)
    {
        AiModelTurn.ValidatePrompt(prompt);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var messages = new List<object> { new { role = "user", content = prompt } };
        var turn = new AiModelTurn();
        for (var round = 1; round <= AiModelTurn.MaxRequests; round++)
        {
            using var response = await SendAsync(messages, turn.CanUseTools(round), probe: false, deadline.Token)
                .ConfigureAwait(false);
            var message = AnthropicMessage.Read(response.RootElement);
            turn.AddUsage(message.Usage);
            var calls = message.ReadCalls();
            if (calls.Length == 0)
            {
                var answer = message.ReadAnswer();
                return new(answer.Text, answer.Refused, turn.Usage, round, turn.ToolCalls);
            }

            turn.AdmitTools(calls.Select(call => call.Id).ToArray(), round, AiModelTurn.MaxToolCalls);
            // Claude 要求在下一条用户消息中成组回传结果，助手内容块须保持原样。
            messages.Add(new { role = "assistant", content = message.Content.Clone() });
            var results = new List<object>(calls.Length);
            foreach (var call in calls)
            {
                var result = await dispatch.ExecuteAsync(call.Name, call.Arguments, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                results.Add(new { type = "tool_result", tool_use_id = call.Id,
                    content = JsonSerializer.Serialize(result, AiModelHttp.Json), is_error = result.Status == "Rejected" });
            }
            messages.Add(new { role = "user", content = results });
        }
        throw new AiModelException("TurnBudgetExceeded");
    }

    public async Task TestConnectionAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await SendAsync([new { role = "user", content = "只回复 OK" }],
            false, probe: true, deadline.Token).ConfigureAwait(false);
        var message = AnthropicMessage.Read(response.RootElement);
        if (message.ReadCalls().Length != 0 || message.ReadAnswer().Refused)
            throw new AiModelException("InvalidProbe");
    }

    private Task<JsonDocument> SendAsync(List<object> messages, bool allowTools, bool probe, CancellationToken token)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = probe ? 128 : 1024,
            ["output_config"] = new { effort = "low" }
        };
        if (!probe)
        {
            payload["system"] = AiModelTurn.Instructions;
            payload["tools"] = Tools;
            // 保留工具结构，使 Claude 能解释前轮结果，同时关闭后续工具选择。
            payload["tool_choice"] = new { type = allowTools ? "auto" : "none" };
        }
        return AiModelHttp.PostAsync(http, Endpoint, key, payload, token, anthropic: true);
    }
}
