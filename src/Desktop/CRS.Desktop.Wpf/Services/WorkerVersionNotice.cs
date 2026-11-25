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
        {
            if (Version.TryParse(health.WorkerVersion, out var version) && version > WorkerProtocol.PairedWorkerVersion &&
                Pairing(health.WorkerBuild) == Pairing(WorkerProtocol.BuildIdentity))
                return null;
            return $"前台与后台版本不一致。{DescribeBuilds(health)}。请更新为同一套版本。";
        }
        return null;
    }

    internal static string DescribeBuilds(BackgroundHealth health) =>
        $"后台服务 {health.WorkerVersion ?? "版本未报告"} · 构建 {Format(health.WorkerBuild)}\n" +
        $"桌面配套 {WorkerProtocol.PairedWorkerVersion} · 构建 {Format(WorkerProtocol.BuildIdentity)}";

    internal static string DescribeUpdate(BackgroundHealth health)
    {
        if (Version.TryParse(health.WorkerVersion, out var installed))
        {
            var comparison = installed.CompareTo(WorkerProtocol.PairedWorkerVersion);
            if (comparison < 0) return "桌面配套版本高于当前后台版本。";
            if (comparison > 0) return "当前后台版本高于此桌面配套版本。";
        }
        if (string.IsNullOrWhiteSpace(health.WorkerBuild)) return "当前后台未报告构建，无法核对配套更新。";
        return string.Equals(health.WorkerBuild, WorkerProtocol.BuildIdentity, StringComparison.Ordinal)
            ? "已与桌面配套版本一致。"
            : "后台与桌面的配套构建不同。";
    }

    private static string Pairing(string identity) => identity.Split('+', 2)[0];

    private static string Format(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "未报告";
        var parts = identity.Split('+', 2);
        if (parts.Length == 1) return parts[0];
        return $"{parts[0]} ({parts[1][..Math.Min(parts[1].Length, 8)]})";
    }
}
