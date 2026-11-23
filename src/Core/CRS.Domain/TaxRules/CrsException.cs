using System.Text.Json.Serialization;

namespace CRS.Domain;

/// <summary>向应用和界面报告可读业务异常，避免未知错误正文直接暴露敏感信息。</summary>
public sealed class CrsException(string message) : Exception(message);
