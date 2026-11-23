namespace CRS.Application.Abstractions;

/// <summary>一次导入的准备与提交边界；计算失败或未提交时不写入任何业务资料。</summary>
public interface IDesktopCalculationSession : IDisposable
{
    CalculationService Calculator { get; }
    void Commit(CalculationResult result, CancellationToken token, bool allowDuplicate = true);
}

public sealed class DesktopCalculationSession(CalculationService calculator,
    Action<CalculationResult,CancellationToken,bool> commit, Action? release = null) : IDesktopCalculationSession
{
    private bool finished;
    public CalculationService Calculator { get; } = calculator;
    public void Commit(CalculationResult result, CancellationToken token, bool allowDuplicate = true)
    {
        // 即使提交失败也不能重用同一准备会话；重试必须重新解析并经过幂等检查。
        if (finished) throw new InvalidOperationException("导入会话已经结束。");
        finished = true;
        token.ThrowIfCancellationRequested();
        commit(result, token, allowDuplicate);
    }
    public void Dispose() { finished = true; release?.Invoke(); }
}
