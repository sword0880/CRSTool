namespace CRS.AI.Query;

public sealed record AiReadPolicyLimits(
    int MaxCallsPerMinute = 20, int MaxConcurrent = 2, int MaxSubjects = 128)
{
    public int MaxTotalCallsPerMinute { get; init; } = 60;
}
