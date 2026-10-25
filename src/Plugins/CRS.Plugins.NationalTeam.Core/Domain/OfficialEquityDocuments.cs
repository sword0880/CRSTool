namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>人工选择并逐页核对的固定发行人原文；绑定证券、报告期和字节摘要，不作拒绝访问后的自动换源。</summary>
public static class OfficialEquityDocuments
{
    public const string IcbcHalfPath="/userfiles/resources/icbcltd/download/2026/InterimReportA20260924.pdf";
    public const string IcbcHalfHash="5e46724bf1e57c6c671350e4a4786e487f3c4f3f61c4b2a08e6e32afa861d4f1";
    // 农行官网A股栏目明确链接的232页原件；固定字节身份，不扩展到ZIP、同目录或其他报告期。
    public const string AbcHalfPath="/cn/aboutabc/investor_relations/report/mm/202608/P020260924668950211207.pdf";
    public const string AbcHalfHash="d94e1793dfe065828107b8671a03eaaa3f57a7dce0a6ceb0cce6f96fe95ca1d5";
    // 原件层按已核字节身份放宽，任意文件/伪造路径不能取得大文件额度；读取后仍必须重新核对摘要。
    public static int MaximumEvidenceBytes(string hash)=>hash.Equals(IcbcHalfHash,StringComparison.OrdinalIgnoreCase)?16*1024*1024:8*1024*1024;
    public static bool IsReviewedIssuer(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0
        &&uri.Query.Length==0&&uri.Fragment.Length==0&&(uri.Host=="www.chinacnd.com"&&uri.AbsolutePath is "/report-227.html" or "/report-226.html"
            ||uri.Host=="v.icbc.com.cn"&&uri.AbsolutePath==IcbcHalfPath||uri.Host=="www.abchina.com"&&uri.AbsolutePath==AbcHalfPath);
    public static bool IsAllowed(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0
        &&(IsReviewedIssuer(uri)||uri.AbsolutePath.EndsWith(".pdf",StringComparison.OrdinalIgnoreCase)
            &&(uri.Host=="sse.com.cn"||uri.Host.EndsWith(".sse.com.cn",StringComparison.Ordinal)||uri.Host=="szse.cn"||uri.Host.EndsWith(".szse.cn",StringComparison.Ordinal)));
    public static bool Matches(Uri uri,string market,string ticker,DateOnly reportAsOf)=>!IsReviewedIssuer(uri)
        ||market=="SSE"&&(uri.Host=="www.abchina.com"?ticker=="601288"&&reportAsOf==new DateOnly(2026,6,30):uri.Host=="v.icbc.com.cn"?ticker=="601398"&&reportAsOf==new DateOnly(2026,6,30)
            :ticker=="600153"&&reportAsOf==(uri.AbsolutePath=="/report-227.html"?new DateOnly(2026,6,30):new DateOnly(2025,12,31)));
    public static Uri IssuerReferrer(Uri uri)=>!IsReviewedIssuer(uri)?throw new InvalidDataException("发行人原文未核对。")
        :new(uri.Host=="www.abchina.com"?"https://www.abchina.com/cn/aboutabc/investor_relations/report/mm/202608/t20260828_2658912.htm":uri.Host=="v.icbc.com.cn"?"https://www.icbc-ltd.com/page/1266821007844429824.html":"https://www.chinacnd.com/report.html");
    // 原地址可能替换内容；仅接受本次逐页核对的工行/农行固定报告，不继承同目录其他文件。
    public static string? ReviewedIssuerHash(Uri uri)=>!IsReviewedIssuer(uri)?null:uri.Host=="www.abchina.com"?AbcHalfHash:uri.Host=="v.icbc.com.cn"
        ?IcbcHalfHash:uri.AbsolutePath=="/report-227.html"
        ?"6d10ee92feb04a3eb7b066b89552dd5c3f84d57c016e0f43738ab270a8a166f9"
        :"470448a5b8073540b0a6a722e1dded1558ab9dc06fb44347d0e91d2948b72df2";
}
