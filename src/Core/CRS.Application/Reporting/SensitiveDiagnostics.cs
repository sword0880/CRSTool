namespace CRS.Application;

/// <summary>诊断只保留固定类别，不采集异常正文、堆栈、内部异常或业务对象。</summary>
public static class SensitiveDiagnostics
{
    public static string FailureCategory(string type)=>type switch
    {
        "CrsException" or "IOException" or "UnauthorizedAccessException" or "OperationCanceledException"
            or "TaskCanceledException" or "CryptographicException" or "JsonException" or "SqliteException"
            or "HttpRequestException" or "TimeoutException" or "InvalidOperationException"=>type,
        _=>"UnexpectedException"
    };
    public static string FormatError(Exception error)=>"CRS 操作失败；类别="+FailureCategory(error.GetType().Name);
}
