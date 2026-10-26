namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>随程序交付的初始化目录，独立于采集事实；新库自动写入，已有用户配置不覆盖。</summary>
public static class InitialEtfDirectory
{
    public const string Version="2026-10-21";
    public const string Origin="内置初始化目录（待核验）";
    // 参考目录23只，加上项目原有的华夏科创50ETF；不作为机构持仓证明。
    public static IReadOnlyList<(string Market,string Ticker,string Name,string Index)> Entries {get;}=
        Array.AsReadOnly(ReferenceEtfDirectory.Entries.Append(("SSE","588000","华夏科创50ETF","STAR50")).ToArray());
}
