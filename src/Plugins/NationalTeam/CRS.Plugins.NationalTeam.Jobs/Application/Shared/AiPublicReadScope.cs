namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>在本插件内传递 AI 只读身份，独立选择名额且不提升原件核验优先级。</summary>
internal static class AiPublicReadScope
{
    private static readonly AsyncLocal<bool> Current = new();
    public static bool IsActive => Current.Value;

    public static IDisposable Enter()
    {
        var scope = new Restore(Current.Value);
        Current.Value = true;
        return scope;
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
