using System.Text.Json;
using CRS.AI.Models.Attachments;
using CRS.AI.Contracts;
using CRS.AI.Models.Tools;

namespace CRS.AI.Models.Providers.Anthropic;

/// <summary>通过 Anthropic 官方 Messages API 调用 Claude，工具仍由本机 Gateway 执行。</summary>
internal sealed class AnthropicMessagesClient(HttpClient http, string key, string model, AiToolDispatch dispatch, bool mimo = false)
    : IAiModelProvider
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/v1/messages");
    private static readonly Uri MiMoEndpoint = new("https://api.xiaomimimo.com/anthropic/v1/messages");
    private static readonly object[] Tools = AiCatalog.Tools.Select(tool =>
        new { name = tool.Name, description = tool.Description, input_schema = tool.Schema }).ToArray();

    public Task<AiModelAnswer> AskAsync(string prompt, CancellationToken token) =>
        AskChatAsync(new(prompt, []), token);

    public async Task<AiModelAnswer> AskChatAsync(AiChatRequest request, CancellationToken token)
    {
        AiModelTurn.Validate(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var messages = new List<object>();
        foreach (var entry in request.History)
        {
            messages.Add(new { role = "user", content = entry.Question });
            messages.Add(new { role = "assistant", content = entry.Answer });
        }

        // 当前问题和当前会话附件内容放入同一条 user 消息；不将原始文件写入本地历史。
        var userText = AiAttachmentPrompt.Compose(request.Prompt, request.Attachments);
        object content = request.Screenshot is { } image ? new object[]
        {
            new { type = "text", text = userText },
            new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = Convert.ToBase64String(image) } }
        } : userText;
        messages.Add(new { role = "user", content });
        var turn = new AiModelTurn();
        for (var round = 1; round <= AiModelTurn.MaxRequests; round++)
        {
            using var response = await SendAsync(messages, turn.CanUseTools(round), probe: false, deadline.Token,
                includesScreenshot: request.Screenshot is not null,
                includesAttachments: request.Attachments is { Count: > 0 })
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

    private Task<JsonDocument> SendAsync(List<object> messages, bool allowTools, bool probe, CancellationToken token,
        bool includesScreenshot = false, bool includesAttachments = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = probe ? 128 : 1024
        };
        if (mimo) payload["thinking"] = new { type = "disabled" };
        else payload["output_config"] = new { effort = "low" };
        if (!probe)
        {
            payload["system"] = AiModelTurn.Instructions;
            if (!mimo || allowTools) payload["tools"] = Tools;
            if (!mimo) payload["tool_choice"] = new { type = allowTools ? "auto" : "none" };
        }
        return AiModelHttp.PostAsync(http, mimo ? MiMoEndpoint : Endpoint, key, payload, token,
            anthropic: !mimo, includesScreenshot: includesScreenshot, includesAttachments: includesAttachments);
    }
}
