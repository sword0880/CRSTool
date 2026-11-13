using CRS.Worker.Abstractions;

namespace CRS.Worker.Service;

internal static class TaskRetryPolicy
{
    internal const int MaximumRetries = 3;
    internal static bool IsFailure(string result) => result is "Failed" or "AccessBlocked" or "SourceSchemaChanged";

    internal static bool ShouldRetry(BackgroundJobResult result, int failures, bool cancellationRequested)
    {
        if (cancellationRequested || failures >= MaximumRetries || !IsFailure(result.Status))
            return false;

        // Runner退出后无法确认外部副作用是否已提交；禁止自动重放结果未知的业务命令。
        return !(result.Status == "Failed" && result.Checkpoint == "RunnerOutcomeUnknown");
    }
}
