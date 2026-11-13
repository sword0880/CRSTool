using System.Text.Json;
using CRS.AI.Models;
using CRS.AI.Models.Providers.Responses;
using CRS.AI.Models.Tools;

namespace CRS.AI.Client.OpenAi;

/// <summary>从环境读取 API 配置，从标准输入读取问题，并输出答案和本次用量。</summary>
internal static class OpenAiAskCommand
{
    public static async Task<int> RunAsync(AiToolDispatch tools, CancellationToken token)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsWhiteSpace) ||
            model is not { Length: > 0 and <= 128 } ||
            model.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or ':' or '.')))
        {
            Console.Error.WriteLine("请通过受控环境配置 OPENAI_API_KEY 和 OPENAI_MODEL。");
            return 2;
        }

        // 通过重定向传入问题，避免问题出现在进程命令行参数中。
        if (!Console.IsInputRedirected)
        {
            Console.Error.WriteLine("请从标准输入提供一条公开 ETF 研究问题。此命令会向 OpenAI 发送问题及所需的公开工具结果。");
            return 2;
        }

        Console.Error.WriteLine("本次请求发送公开 ETF 问题及白名单工具结果到 OpenAI。请求设置 store=false。");
        // 禁止自动重定向和 Cookie，避免跟随服务端跳转或跨请求保留会话状态。
        using var transport = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var http = new HttpClient(transport);
        try
        {
            var buffer = new char[AiModelSettings.MaxPromptLength + 1];
            var count = await Console.In.ReadBlockAsync(buffer.AsMemory(), token);
            if (count > AiModelSettings.MaxPromptLength)
                throw new AiModelException("InvalidPrompt");
            var result = await new ResponsesApiClient(http, key, model, tools).AskAsync(new string(buffer, 0, count), token);
            // 模型输出不允许用终端控制字符改变显示或触发控制序列。
            Console.WriteLine(string.Concat(result.Text.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t')));
            Console.Error.WriteLine($"模型请求 {result.Requests} 次，工具调用 {result.ToolCalls} 次，输入 {result.Usage.InputTokens}、输出 {result.Usage.OutputTokens} tokens。");
            return result.Refused ? 1 : 0;
        }
        catch (AiModelException error)
        {
            Console.Error.WriteLine("OpenAI 请求未完成（" + error.Code + "）。");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
                                      JsonException or OperationCanceledException)
        {
            Console.Error.WriteLine("OpenAI 请求未完成，请检查连接、时限及响应格式。");
        }
        return 1;
    }
}
