namespace CRS.Worker.Service;

internal static class TaskRetryPolicy
{
    internal const int MaximumRetries = 3;
    internal static bool IsFailure(string result) => result is "Failed" or "AccessBlocked" or "SourceSchemaChanged";
}
