namespace CRS.Infrastructure;

/// <summary>失败日志使用固定允许类别，不把调用方传入的字符串当作可记录正文。</summary>
internal static class SensitiveLog
{
    internal static string FailureCategory(string type)=>SensitiveDiagnostics.FailureCategory(type);
}
