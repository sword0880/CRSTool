using System.Buffers.Binary;
using System.Text.Json;
using CRS.Worker.Abstractions;
using CRS.Worker.PluginHost;

namespace CRS.Worker.PluginRunner;

/// <summary>
/// 进程入口：正式宿主使用--supervised进入V2监督协议；无该参数时保留旧V1原型供兼容测试。
/// </summary>
internal static class Program
{
    private const int ProtocolVersion = 1;
    private const int MaximumFrameBytes = 131072;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<int> Main(string[] args)
    {
        if(args.Contains("--maintenance",StringComparer.Ordinal))
            return args.Contains("--supervised",StringComparer.Ordinal)?1:await MaintenanceRunner.RunAsync(args);
        if(args.Contains("--supervised",StringComparer.Ordinal))return await SupervisedRunner.RunAsync(args);
        try
        {
            // Reserve stdout exclusively for framed protocol bytes before plugin startup.
            // Plugin/host logs go to stderr and can never corrupt the first response frame.
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            Console.SetOut(Console.Error);

            var root = Path.GetFullPath(Option(args, "--plugin-root"));
            var trustFile = Path.GetFullPath(Option(args, "--trust-file"));
            var pluginId = Option(args, "--plugin-id");
            if (!ValidPluginId(pluginId) || !Directory.Exists(root) || !File.Exists(trustFile))
                throw new InvalidDataException("Runner startup configuration is invalid.");

            var packages = JsonSerializer.Deserialize<TrustedPackage[]>(await File.ReadAllTextAsync(trustFile), Json)
                ?? throw new InvalidDataException("Runner trust index is empty.");
            if (packages.Length != 1 || packages[0].Id != pluginId
                || string.IsNullOrWhiteSpace(packages[0].Directory))
                throw new InvalidDataException("Runner must receive exactly one explicitly bound package.");

            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var packagePath = Path.GetFullPath(Path.Combine(root, packages[0].Directory.Replace('/', Path.DirectorySeparatorChar)));
            if (!packagePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runner package path escaped its root.");

            await using var catalog = new BackgroundPluginCatalog(
                lifecycleTimeout: TimeSpan.FromSeconds(15),
                onUnresponsive: () => Environment.Exit(2));
            await catalog.LoadAsync(root, trustFile, CancellationToken.None);
            var status = catalog.Statuses.SingleOrDefault(x => x.PluginId == pluginId);
            if (catalog.Statuses.Count != 1 || status?.State != "Started")
                throw new InvalidDataException("Runner plugin failed startup.");

            while (true)
            {
                var request = await ReadRequestAsync(input, CancellationToken.None);
                if (request is null) break;
                var stop = false;
                RunnerResponse response;
                if (request.ProtocolVersion != ProtocolVersion)
                {
                    response = Reply(request, "ProtocolMismatch");
                }
                else if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Command)
                    || request.Command.Length > 24 || request.JobId?.Length > 80 || request.Payload?.Length > 32768)
                {
                    response = Reply(request, "InvalidRequest");
                }
                else
                {
                    try
                    {
                        switch (request.Command)
                        {
                            case "Heartbeat":
                                response = Reply(request, "Ok");
                                break;
                            case "Describe":
                                response = new(ProtocolVersion, request.RequestId, "Ok",
                                    catalog.Jobs.Where(x => x.PluginId == pluginId).Select(x => x.Descriptor).ToArray());
                                break;
                            case "Execute":
                            {
                                if (string.IsNullOrWhiteSpace(request.JobId))
                                {
                                    response = Reply(request, "InvalidRequest");
                                    break;
                                }
                                var job = catalog.Jobs.SingleOrDefault(x => x.PluginId == pluginId && x.Descriptor.JobId == request.JobId);
                                if (job is null)
                                {
                                    response = Reply(request, "UnknownJob");
                                    break;
                                }
                                if (job.Session is IProgressBackgroundSession progressSession)
                                {
                                    var updates = new List<BackgroundProgress>(8);
                                    var truncated = false;
                                    var progressResult = await progressSession.ExecuteWithProgressAsync(request.JobId, request.Payload, progress =>
                                    {
                                        if (updates.Count < 64) updates.Add(progress);
                                        else truncated = true;
                                        return Task.CompletedTask;
                                    }, CancellationToken.None, request.RequestId);
                                    response = new(ProtocolVersion, request.RequestId, "Ok", Result: progressResult,
                                        Progress: updates, ProgressTruncated: truncated);
                                    break;
                                }
                                var result = job.Session is IParameterizedBackgroundSession parameterized
                                    ? await parameterized.ExecuteAsync(request.JobId, request.Payload, CancellationToken.None, request.RequestId)
                                    : await job.Session.ExecuteAsync(request.JobId, CancellationToken.None);
                                response = new(ProtocolVersion, request.RequestId, "Ok", Result: result);
                                break;
                            }
                            case "Query":
                                response = new(ProtocolVersion, request.RequestId, "Ok",
                                    Payload: await catalog.QueryAsync(pluginId, request.Payload ?? "", CancellationToken.None));
                                break;
                            case "Shutdown":
                                response = Reply(request, "Ok");
                                stop = true;
                                break;
                            default:
                                response = Reply(request, "UnknownCommand");
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        response = Reply(request, "Cancelled");
                    }
                    catch (Exception error)
                    {
                        response = new(ProtocolVersion, request.RequestId, "PluginFailure", ErrorType: error.GetType().Name);
                    }
                }
                await WriteResponseAsync(output, response, CancellationToken.None);
                if (stop) break;
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException
            or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("PLUGIN_RUNNER_FATAL:" + error.GetType().Name);
            return 1;
        }
    }

    private static RunnerResponse Reply(RunnerRequest request, string code) =>
        new(ProtocolVersion, request.RequestId, code);

    private static string Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException("Missing runner option.");
        return args[index + 1];
    }

    private static bool ValidPluginId(string value) => value.Length is > 0 and <= 80
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static async Task<RunnerRequest?> ReadRequestAsync(Stream input, CancellationToken token)
    {
        var header = new byte[4];
        var read = await input.ReadAsync(header.AsMemory(), token);
        if (read == 0) return null;
        while (read < header.Length)
        {
            var count = await input.ReadAsync(header.AsMemory(read), token);
            if (count == 0) throw new EndOfStreamException("Runner frame header was truncated.");
            read += count;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Runner frame length is invalid.");
        var body = new byte[length];
        read = 0;
        while (read < body.Length)
        {
            var count = await input.ReadAsync(body.AsMemory(read), token);
            if (count == 0) throw new EndOfStreamException("Runner frame body was truncated.");
            read += count;
        }
        return JsonSerializer.Deserialize<RunnerRequest>(body, Json)
            ?? throw new InvalidDataException("Runner request is empty.");
    }

    private static async Task WriteResponseAsync(Stream output, RunnerResponse response, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        if (body.Length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("Runner response exceeds frame budget.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await output.WriteAsync(header, token);
        await output.WriteAsync(body, token);
        await output.FlushAsync(token);
    }
}

internal sealed record RunnerRequest(int ProtocolVersion, Guid RequestId, string Command,
    string? JobId = null, string? Payload = null);
internal sealed record RunnerResponse(int ProtocolVersion, Guid RequestId, string Code,
    IReadOnlyList<BackgroundJobDescriptor>? Jobs = null, BackgroundJobResult? Result = null,
    IReadOnlyList<BackgroundProgress>? Progress = null, bool ProgressTruncated = false,
    string? Payload = null, string? ErrorType = null);
