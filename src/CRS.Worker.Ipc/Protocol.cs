using System.Buffers.Binary;
using System.Text.Json;
using CRS.Worker.Abstractions;

namespace CRS.Worker.Ipc;

/// <summary>协议只接受固定命令和标量参数，不接收路径、类型名或客户端自报权限。</summary>
public sealed record BackgroundRequest(int ProtocolVersion, Guid RequestId, string Command,
    string? PluginId = null, string? JobId = null, string? Cron = null, string? TimeZoneId = null,
    long? ExpectedRevision = null, string? Cursor = null, int PageSize = 10, Guid? RelatedRequestId = null,string? Payload=null,string? ExecutionId=null);
public sealed record BackgroundPluginScheduling(string PluginId, int MaxConcurrency, int Running, int Waiting);
public sealed record BackgroundScheduling(int MaxConcurrency, int Running, int Waiting, int MaxPendingPerPlugin, long CoalescedTriggers,
    long CapacityDeferredTriggers, int PendingManualCommands = 0, IReadOnlyList<BackgroundPluginScheduling>? Plugins = null);
public sealed record BackgroundHealth(string State, DateTimeOffset StartedUtc, DateTimeOffset ObservedUtc, int ProcessId, BackgroundScheduling? Scheduling = null,
    IReadOnlyList<SourceBudgetHealth>? Sources = null);
public sealed record BackgroundResponse(int ProtocolVersion, Guid RequestId, string Code,
    BackgroundHealth? Health = null, IReadOnlyList<TaskDefinition>? Jobs = null,
    IReadOnlyList<ExecutionRecord>? History = null, string? NextCursor = null,
    CommandReceipt? Receipt = null, IReadOnlyList<BackgroundPluginStatus>? Plugins = null,string? Payload=null,ExecutionRecord? Execution=null);

/// <summary>分页与持久命令状态属于第二版协议；旧客户端明确拒绝，固定管道名保持可诊断。</summary>
public static class WorkerProtocol { public const int Version = 2; }

/// <summary>服务端使用实际 Windows 令牌构造调用者，权限字段不进入请求协议。</summary>
public sealed record BackgroundCaller(string Sid, bool CanManage);
public interface IBackgroundControl
{
    Task<BackgroundResponse> HandleAsync(BackgroundRequest request, BackgroundCaller caller, CancellationToken token);
}

internal static class Framing
{
    public const int Version = WorkerProtocol.Version;
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > 131072) throw new InvalidDataException("管道消息长度超过上限。");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body) ?? throw new InvalidDataException("管道消息格式不正确。");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        if (body.Length > 131072) throw new InvalidDataException("响应超过上限。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(body, token); await stream.FlushAsync(token);
    }
}
