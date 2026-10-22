using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Worker.Abstractions;

internal static class RunnerProcessChecks
{
    private const int ProtocolVersion = 1;
    private const int MaximumFrameBytes = 131072;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static int passed;

    private sealed record Request(int ProtocolVersion, Guid RequestId, string Command, string? JobId = null, string? Payload = null);
    private sealed record Response(int ProtocolVersion, Guid RequestId, string Code,
        IReadOnlyList<BackgroundJobDescriptor>? Jobs = null, BackgroundJobResult? Result = null, string? Payload = null, string? ErrorType = null);

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        passed++;
        Console.WriteLine("PASS PluginRunner: " + name);
    }

    public static async Task RunAsync(string runnerDll)
    {
        passed = 0;
        var root = Path.Combine(Path.GetTempPath(), "CRS.PluginRunner.ProcessTests-" + Guid.NewGuid().ToString("N"));
        Process? process = null;
        try
        {
            var package = WorkerRemediationChecks.Package(root, ("crs.fixture", "ConsoleWritingEntry"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(Path.GetFullPath(runnerDll));
            start.ArgumentList.Add("--plugin-root");
            start.ArgumentList.Add(package.Root);
            start.ArgumentList.Add("--trust-file");
            start.ArgumentList.Add(package.Trust);
            start.ArgumentList.Add("--plugin-id");
            start.ArgumentList.Add("crs.fixture");
            process = Process.Start(start) ?? throw new InvalidOperationException("PluginRunner process did not start.");
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var describe = await ExchangeAsync(process, new(ProtocolVersion, Guid.NewGuid(), "Describe"), deadline.Token);
            Check(describe.Code == "Ok" && describe.Jobs?.Any(x => x.JobId == "one") == true,
                "Runner 在独立进程中加载受信插件并返回任务描述");

            var execute = await ExchangeAsync(process, new(ProtocolVersion, Guid.NewGuid(), "Execute", "one"), deadline.Token);
            Check(execute.Code == "Ok" && execute.Result?.Status == "Succeeded",
                "Runner 执行任务并返回有界结果 DTO");

            var query = await ExchangeAsync(process, new(ProtocolVersion, Guid.NewGuid(), "Query", Payload: "ok"), deadline.Token);
            Check(query.Code == "Ok" && query.Payload == "fixture-query-ok",
                "Runner 独立处理插件查询");

            var unknownJob = await ExchangeAsync(process, new(ProtocolVersion, Guid.NewGuid(), "Execute", "missing"), deadline.Token);
            Check(unknownJob.Code == "UnknownJob", "未知 Job 显式失败且进程保持可用");

            var unknownCommand = await ExchangeAsync(process, new(ProtocolVersion, Guid.NewGuid(), "DropEverything"), deadline.Token);
            Check(unknownCommand.Code == "UnknownCommand", "未知内部命令被拒绝");

            var wrongVersion = await ExchangeAsync(process, new(999, Guid.NewGuid(), "Describe"), deadline.Token);
            Check(wrongVersion.Code == "ProtocolMismatch", "未知 Runner 协议版本明确拒绝且不降级");

            var shutdown = await ExchangeAsync(process, new(ProtocolVersion, Guid.NewGuid(), "Shutdown"), deadline.Token);
            Check(shutdown.Code == "Ok", "显式关闭命令收到确认");
            await process.WaitForExitAsync(deadline.Token);
            var stderr = await stderrTask;
            Check(stderr.Contains("synthetic plugin startup log", StringComparison.Ordinal)
                && stderr.Contains("synthetic plugin execution log", StringComparison.Ordinal),
                "插件标准输出被重定向到 stderr，不污染协议帧");
            Check(process.ExitCode == 0, "Runner 正常释放插件并退出（stderr 类别长度=" + stderr.Length + "）");
            Console.WriteLine("PluginRunner 原型进程验收通过：" + passed + " 项。此原型只验证进程故障域与协议帧，不代表 OS 安全沙箱。");
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
            if (Directory.Exists(root))
            {
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(root);
                if (full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).StartsWith("CRS.PluginRunner.ProcessTests-", StringComparison.Ordinal))
                    Directory.Delete(full, recursive: true);
                else throw new InvalidOperationException("Refusing to remove test data outside the unique temp root.");
            }
        }
    }

    private static async Task<Response> ExchangeAsync(Process process, Request request, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (body.Length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Test request exceeds frame limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        var input = process.StandardInput.BaseStream;
        await input.WriteAsync(header, token);
        await input.WriteAsync(body, token);
        await input.FlushAsync(token);

        var output = process.StandardOutput.BaseStream;
        var responseHeader = new byte[4];
        await ReadExactlyAsync(output, responseHeader, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(responseHeader);
        if (length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Runner response frame length is invalid.");
        var responseBody = new byte[length];
        await ReadExactlyAsync(output, responseBody, token);
        var response = JsonSerializer.Deserialize<Response>(responseBody, Json)
            ?? throw new InvalidDataException("Runner response is empty.");
        if (response.RequestId != request.RequestId) throw new InvalidDataException("Runner response RequestId mismatch.");
        return response;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], token);
            if (count == 0) throw new EndOfStreamException("Runner closed before a complete response frame.");
            offset += count;
        }
    }
}
