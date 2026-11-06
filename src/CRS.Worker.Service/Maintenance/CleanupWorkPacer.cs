using System.Diagnostics;
namespace CRS.Worker.Service;

/// <summary>后台自动清理让出CPU；不在状态锁内休息，关闭/升级取消会立即唤醒等待。</summary>
internal sealed class CleanupWorkPacer(CancellationToken token, double dutyCycle = .1)
{
    private readonly Stopwatch work = Stopwatch.StartNew();
    internal void Checkpoint()
    {
        if(!double.IsFinite(dutyCycle)||dutyCycle is <.01 or >1)throw new ArgumentOutOfRangeException(nameof(dutyCycle));
        token.ThrowIfCancellationRequested();
        if (dutyCycle == 1 || work.Elapsed < TimeSpan.FromMilliseconds(20)) return;
        var pause = TimeSpan.FromTicks((long)(work.Elapsed.Ticks * (1 / dutyCycle - 1)));
        if (token.WaitHandle.WaitOne(pause)) token.ThrowIfCancellationRequested();
        work.Restart();
    }
}
