namespace CRS.Domain;

/// <summary>已应用汇率的值和来源事实；文件、网络和缓存均由基础设施实现。</summary>
public sealed record AppliedRate(int Year, string Currency, decimal Value, string Source, string Url,
    string Method, DateOnly? Start, DateOnly? End, bool Provisional, bool SourceConfirmed);
