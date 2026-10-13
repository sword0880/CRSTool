namespace CRS.Infrastructure;

/// <summary>失败日志使用固定允许类别，不把调用方传入的字符串当作可记录正文。</summary>
internal static class SensitiveLog
{
    internal static string FailureCategory(string type)=>type switch
    {
        "CrsException" or "IOException" or "UnauthorizedAccessException" or "OperationCanceledException"
            or "TaskCanceledException" or "CryptographicException" or "JsonException" or "SqliteException"
            or "HttpRequestException" or "TimeoutException" or "InvalidOperationException"=>type,
        _=>"UnexpectedException"
    };
}
