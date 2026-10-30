// 原件核验读取的有界准入，控制并发、等待与取消，结束时排空租约避免泄漏文件句柄。
using CRS.Common.Concurrency;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>
/// Existing NationalTeamWatch API preserved as an adapter to the common CRS plugin admission mechanism.
/// Plugin instance controls its own quota; the original evidence, audit and Worker IPC types stay local.
/// </summary>
internal sealed class RawEvidenceReadAdmission
{
    private readonly PriorityReadAdmission shared;
    public RawEvidenceReadAdmission(int capacity)=>shared=new(capacity,8);
    public RawEvidenceReadActivity Activity
    {
        get
        {
            var status=shared.Activity;
            return new(status.Active,status.PriorityWaiting,status.BackgroundWaiting);
        }
    }
    public ValueTask<IDisposable> EnterAsync(bool query,CancellationToken token)=>shared.EnterAsync(query,token);
}

/// <summary>Compatibility telemetry; no evidence paths, content or secrets.</summary>
public sealed record RawEvidenceReadActivity(int Active,int QueryWaiting,int BackgroundWaiting);
