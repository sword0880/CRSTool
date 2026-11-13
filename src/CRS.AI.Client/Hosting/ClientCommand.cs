using System.Globalization;
using CRS.AI.Client.Mcp;
using CRS.AI.Client.OpenAi;
using CRS.AI.Models.Tools;
using CRS.AI.Gateway.Transport;
using Microsoft.Extensions.Hosting;

namespace CRS.AI.Client.Hosting;

/// <summary>解析命令行参数，启动本机 MCP 服务或执行一次 OpenAI 查询。</summary>
internal static class ClientCommand
{
    private const int DefaultPort = 8765;

    /// <summary>将 Ctrl+C 传递给正在运行的服务或查询，并在退出时注销事件处理。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler interrupt = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += interrupt;
        try
        {
            var tools = new AiToolDispatch(new WindowsAiReadClient().ExecuteAsync);
            return args switch
            {
                ["--ask"] => await OpenAiAskCommand.RunAsync(tools, shutdown.Token),
                ["--mcp"] => await RunMcpAsync(DefaultPort, tools, shutdown.Token),
                ["--mcp", var value] when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                    out var port) && port is >= 1024 and <= 65535 =>
                    await RunMcpAsync(port, tools, shutdown.Token),
                ["--help"] => PrintUsage(0),
                _ => PrintUsage(2)
            };
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 130; }
        finally { Console.CancelKeyPress -= interrupt; }
    }

    /// <summary>验证本机凭证配置，启动 MCP 监听并等待关闭。</summary>
    private static async Task<int> RunMcpAsync(int port, AiToolDispatch tools, CancellationToken cancellation)
    {
        var token = Environment.GetEnvironmentVariable("CRS_MCP_TOKEN");
        if (!LoopbackMcpSecurity.IsTokenFormatValid(token))
        {
            Console.Error.WriteLine("请通过 CRS_MCP_TOKEN 配置 32 字节随机值的 Base64URL 编码（43 字符）。");
            return 2;
        }

        try
        {
            await using var app = McpHttpHost.Create(port, token!, tools);
            await app.StartAsync(cancellation);
            Console.Error.WriteLine($"CRS MCP 正在监听 http://127.0.0.1:{port}/mcp。");
            await app.WaitForShutdownAsync(cancellation);
            return 0;
        }
        catch (IOException)
        {
            Console.Error.WriteLine($"无法监听本机端口 {port}，请检查端口是否已占用。");
            return 1;
        }
    }

    private static int PrintUsage(int exitCode)
    {
        Console.Error.WriteLine("用法：CRS.AI.Client --mcp [端口] 或 CRS.AI.Client --ask（从标准输入读取问题）");
        return exitCode;
    }
}
