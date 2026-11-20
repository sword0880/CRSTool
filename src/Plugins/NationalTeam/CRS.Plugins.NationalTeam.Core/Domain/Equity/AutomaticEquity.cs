namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>官方证券目录仅绑定代码和披露主体，不作为机构持股证据。</summary>
public sealed record AutomaticEquitySecurity(string Market,string Ticker,string Name,string OrgId);
public sealed record AutomaticEquityDocument(string Id,string Market,string Ticker,string Name,string OrgId,string Title,
    DateOnly Published,DateOnly ReportAsOf,string Url,int DeclaredKilobytes=0);
public sealed record AutomaticEquityCursor(DateOnly From,DateOnly Through,DateOnly? Date,int Page,int? DayTotal,bool Complete);
public sealed record AutomaticEquityStatus(AutomaticEquityCursor? Cursor,int Securities,int Indexed,int Pending,int Verified,int NoMatch,
    int NeedsReview,DateTimeOffset? UpdatedAt,string? LastIssue,DateTimeOffset? SecuritiesCheckedAt=null)
{
    public string DisplayText=>Cursor is null?"自动持股发现尚未完成首次取数。"+(LastIssue is null?"":" "+LastIssue):$"自动发现：已登记{Indexed}份报告，已核{Verified}份，无匹配账户{NoMatch}份，待处理{Pending}份，异常待核{NeedsReview}份。"
        +(Cursor.Complete&&Pending==0?"本轮检索处理完成。":$"检索日期{Cursor.Date:yyyy-MM-dd}；进度不代表全市场持仓完整。")
        +(LastIssue is null?"":" 最近异常："+LastIssue);
}
public sealed record AutomaticEquityProof(IReadOnlyList<EquityHolder> Holders,IReadOnlyList<AutomaticEquityAccount> Accounts,string Pages,IReadOnlyList<string>? Issues=null);
public sealed record AutomaticEquityAccount(string Account,string Institution,string Page);
public static class AutomaticEquityBasis
{
    public static bool ConfirmedA(string shareClass,decimal total,decimal? unrestricted)
        =>total>0&&(shareClass is "A股" or "人民币普通股"||shareClass=="Unknown"&&unrestricted==total);
}
