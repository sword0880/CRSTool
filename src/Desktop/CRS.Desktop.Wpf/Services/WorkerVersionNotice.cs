using CRS.Worker.Ipc;

namespace CRS.DesktopClient.Services;

internal static class WorkerVersionNotice
{
    internal static string? GetWarning(BackgroundResponse response)
    {
        if (response.Code == "ProtocolMismatch")
            return "前台与后台通信版本不一致。请更新为同一套版本。";
        if (response.Code != "Ok" || response.Health is not { } health) return null;
        if (string.IsNullOrWhiteSpace(health.WorkerBuild))
            return "当前 Worker 版本较旧，无法核对配套版本。请更新后台服务。";
        if (!string.Equals(health.WorkerBuild, WorkerProtocol.BuildIdentity, StringComparison.Ordinal))
            return $"前台与后台版本不一致。{DescribeBuilds(health)}。请更新为同一套版本。";
        return null;
    }

    internal static string DescribeBuilds(BackgroundHealth health) =>
        $"桌面端 {Format(WorkerProtocol.BuildIdentity)} · Worker {Format(health.WorkerBuild)}";

    private static string Format(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "未报告";
        var parts = identity.Split('+', 2);
        if (parts.Length == 1) return parts[0];
        return $"{parts[0]} ({parts[1][..Math.Min(parts[1].Length, 8)]})";
    }
}
