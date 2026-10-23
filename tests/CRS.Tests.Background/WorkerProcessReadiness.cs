using System.Diagnostics;
using CRS.Worker.Ipc;

/// <summary>进程创建不等于应用已就绪；只在启动期限内重试尚未连接的只读健康请求。</summary>
internal static class WorkerProcessReadiness
{
    internal static async Task<BackgroundResponse> WaitAsync(BackgroundPipeClient client, Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("隔离Worker提前退出：" + process.ExitCode);
            try { return await client.QueryAsync("GetHealth", deadline.Token); }
            catch (BackgroundServiceException error) when (error.Code == "ServiceUnavailable")
            { await Task.Delay(50, deadline.Token); }
        }
    }
}
