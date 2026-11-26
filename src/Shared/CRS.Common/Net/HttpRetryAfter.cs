using System.Net.Http.Headers;

namespace CRS.Common.Net;

public static class HttpRetryAfter
{
    public static TimeSpan? GetDelay(RetryConditionHeaderValue? retryAfter, DateTimeOffset now) =>
        retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : null);
}
