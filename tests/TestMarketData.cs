using CRS.Plugins.NationalTeamWatch.Infrastructure;
/// <summary>仅测试使用的审批审计认证密钥；国家队业务数据库使用普通SQLite。</summary>
internal static class TestMarketData
{
    public static readonly byte[] Key=Enumerable.Range(1,32).Select(x=>(byte)x).ToArray();
    public static MarketRepository Open(string directory,RawEvidenceStore raw,Action? beforeWrite=null,TimeProvider? timeProvider=null)=>
        new(directory,raw,beforeWrite,Key,timeProvider);
}
