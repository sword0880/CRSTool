using CRS.DesktopClient.Services;
using Microsoft.Win32;

internal static class AutomaticLockChecks
{
    /// <summary>用可控单调时钟验证十分钟边界，不等待真实十分钟或改变 Windows 会话。</summary>
    internal static void Run()
    {
        var clock=new Clock(); var policy=new AutomaticLockPolicy(clock);
        clock.Advance(TimeSpan.FromMinutes(10)-TimeSpan.FromTicks(1));
        if(policy.IsExpired) throw new Exception("未满十分钟就自动锁定。");
        clock.Advance(TimeSpan.FromTicks(1)); if(!policy.IsExpired) throw new Exception("十分钟边界未锁定。");
        policy.RecordActivity(); if(policy.IsExpired) throw new Exception("真实输入未重置计时。");
        clock.Advance(TimeSpan.FromMinutes(11)); if(!policy.IsExpired) throw new Exception("超时未锁定。");
        foreach(var reason in new[] {SessionSwitchReason.SessionLock,SessionSwitchReason.SessionLogoff,SessionSwitchReason.ConsoleDisconnect,SessionSwitchReason.RemoteDisconnect})
            if(!AutomaticLockPolicy.RequiresLock(reason)) throw new Exception("系统会话断开未触发锁定。");
        if(AutomaticLockPolicy.RequiresLock(SessionSwitchReason.SessionUnlock) || AutomaticLockPolicy.RequiresLock(PowerModes.StatusChange)
            || !AutomaticLockPolicy.RequiresLock(PowerModes.Suspend) || !AutomaticLockPolicy.RequiresLock(PowerModes.Resume))
            throw new Exception("锁屏／休眠事件策略错误。");
        Console.WriteLine("自动锁定通过：十分钟边界、真实输入重置、锁屏／注销／断开、休眠／恢复及非锁定事件。");
    }
    private sealed class Clock:TimeProvider
    {
        private long now;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>now;
        public void Advance(TimeSpan duration)=>now+=duration.Ticks;
    }
}
