namespace CRS.Application.Performance;

public sealed record PerformanceBenchmark(string Symbol,string Name,string Ticker,string Currency);

/// <summary>基准的供应商代码和原币定义由行情获取与缓存校验共用。</summary>
public static class PerformanceBenchmarks
{
    public static PerformanceBenchmark Get(string symbol)=>symbol switch
    {
        "SSE"=>new("SSE","上证指数","000001.SS","CNY"),
        "SPX"=>new("SPX","标普500","^GSPC","USD"),
        "HSI"=>new("HSI","恒生指数","^HSI","HKD"),
        "IXIC"=>new("IXIC","纳斯达克综合","^IXIC","USD"),
        _=>throw new CrsException("不支持此对比指数。")
    };
}
