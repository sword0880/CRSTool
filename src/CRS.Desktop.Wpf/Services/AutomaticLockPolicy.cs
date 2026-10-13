using Microsoft.Win32;

namespace CRS.DesktopClient.Services;

/// <summary>自动锁定使用单调时间；重建工作区后重新计时，系统校时不能延长会话。</summary>
public sealed class AutomaticLockPolicy(TimeProvider? time=null)
{
    public static readonly TimeSpan Timeout=TimeSpan.FromMinutes(10);
    private readonly TimeProvider clock=time??TimeProvider.System;
    private long lastActivity=(time??TimeProvider.System).GetTimestamp();
    public void RecordActivity()=>lastActivity=clock.GetTimestamp();
    public bool IsExpired=>clock.GetElapsedTime(lastActivity)>=Timeout;
    public static bool RequiresLock(SessionSwitchReason reason)=>reason is SessionSwitchReason.SessionLock
        or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect;
    public static bool RequiresLock(PowerModes mode)=>mode is PowerModes.Suspend or PowerModes.Resume;
}
