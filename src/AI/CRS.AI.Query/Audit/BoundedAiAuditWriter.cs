using System.Diagnostics;

namespace CRS.AI.Query;

/// <summary>为同步阻塞或忽略取消的审计写入保留一个独立槽位。</summary>
internal sealed class BoundedAiAuditWriter(IAiReadAuditSink sink)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim slot = new(1, 1);

    public async Task<bool> WriteAsync(AiReadAuditEvent entry)
    {
        var started = Stopwatch.GetTimestamp();
        if (!await slot.WaitAsync(Timeout)) return false;
        var remaining = Timeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            slot.Release();
            return false;
        }

        // 写入器可能在返回 Task 前就阻塞。写入真正结束前保留槽位，避免超时堆积后台任务。
        var deadline = new CancellationTokenSource(remaining);
        var token = deadline.Token;
        var writing = Task.Run(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await sink.WriteAsync(entry, token);
                return !token.IsCancellationRequested;
            }
            catch (Exception) { return false; }
            finally
            {
                deadline.Dispose();
                slot.Release();
            }
        });
        // 等待期限独立于写入器的取消回调，避免回调卡住时拖延拒绝响应。
        try { return await writing.WaitAsync(remaining); }
        catch (TimeoutException) { return false; }
    }
}
