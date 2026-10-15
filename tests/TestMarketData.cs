using CRS.Plugins.NationalTeamWatch.Infrastructure;
/// <summary>仅测试使用的显式固定密钥；生产密钥由 Worker 的机器保护主密钥派生。</summary>
internal static class TestMarketData
{
    public static readonly byte[] Key=Enumerable.Range(1,32).Select(x=>(byte)x).ToArray();
    public static MarketRepository Open(string directory,RawEvidenceStore raw,Action? beforeWrite=null)=>
        new(directory,raw,beforeWrite,Key);
}
