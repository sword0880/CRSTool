namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>人工选择的官方原文来源；发行人例外只覆盖已逐页核对的两份固定报告，不作拒绝访问后的自动换源。</summary>
public static class OfficialEquityDocuments
{
    public static bool IsReviewedIssuer(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Host=="www.chinacnd.com"
        &&uri.Query.Length==0&&uri.Fragment.Length==0&&uri.AbsolutePath is "/report-227.html" or "/report-226.html";
    public static bool IsAllowed(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0
        &&(IsReviewedIssuer(uri)||uri.AbsolutePath.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase)
            &&(uri.Host=="sse.com.cn"||uri.Host.EndsWith(".sse.com.cn",StringComparison.Ordinal)||uri.Host=="szse.cn"||uri.Host.EndsWith(".szse.cn",StringComparison.Ordinal)));
    public static bool Matches(Uri uri,string market,string ticker,DateOnly reportAsOf)=>!IsReviewedIssuer(uri)
        ||market=="SSE"&&ticker=="600153"&&reportAsOf==(uri.AbsolutePath=="/report-227.html"?new DateOnly(2026,6,30):new DateOnly(2025,12,31));
    // 发行人链接可在原地址替换内容；人工确认仅对这两份原件字节有效。
    public static string? ReviewedIssuerHash(Uri uri)=>!IsReviewedIssuer(uri)?null:uri.AbsolutePath=="/report-227.html"
        ?"6d10ee92feb04a3eb7b066b89552dd5c3f84d57c016e0f43738ab270a8a166f9"
        :"470448a5b8073540b0a6a722e1dded1558ab9dc06fb44347d0e91d2948b72df2";
}
