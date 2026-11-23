namespace CRS.Application;

/// <summary>账户生命周期由用户核对原件后声明；摘要用于追溯，不自动认证券商来源。</summary>
public sealed record AccountPeriodEvidence(string Account,DateOnly Opened,DateOnly? Closed,
    bool OpeningZeroConfirmed,string EvidenceName,string EvidenceSha256,bool Reviewed);
public sealed record AccountCoverage(string Account,DateOnly Start,DateOnly End,string Kind,bool PeriodComplete);

internal static class AccountPeriodPolicy
{
    internal static (DateOnly Start,DateOnly End) Window(ImportData data,string account,DateOnly begin,DateOnly end)
    {
        var item=data.AccountPeriods.SingleOrDefault(p=>p.Account==account);
        return item is null ? (begin,end) : (item.Opened>begin?item.Opened:begin,item.Closed is { } close && close<end?close:end);
    }
    internal static void Validate(ImportData data,int year,string[] accounts)
    {
        if(data.AccountPeriods.Count>1000 || data.AccountPeriods.Any(p=>p is null) || data.AccountPeriods.Select(p=>p.Account).Distinct().Count()!=data.AccountPeriods.Count)
            throw new CrsException("账户期间重复或数量超限。");
        foreach(var p in data.AccountPeriods)
        {
            if(!p.Reviewed || !accounts.Contains(p.Account) || p.Opened.Year is <2000 or >2100
                || p.Opened>new DateOnly(year,12,31) || p.Closed is { } close && (close.Year>2100 || close<p.Opened || close<new DateOnly(year,1,1))
                || string.IsNullOrWhiteSpace(p.EvidenceName) || p.EvidenceName.Length>256
                || p.EvidenceName.IndexOfAny(['/', '\\', ':'])>=0
                || p.EvidenceSha256 is not {Length:64} || p.EvidenceSha256.Any(c=>!Uri.IsHexDigit(c)))
                throw new CrsException("账户期间、归属或已核对的凭证依据无效。");
            bool Outside(DateOnly date)=>date<p.Opened || p.Closed is { } closed && date>closed;
            if(data.Trades.Concat(data.ReportedSales).Any(t=>t.Key.Account==p.Account && Outside(DateOnly.FromDateTime(t.Time.Date)))
                || data.Cash.Any(c=>c.Account==p.Account && Outside(c.Date))
                || data.OpeningLots.Any(l=>l.Key.Account==p.Account && Outside(DateOnly.FromDateTime(l.BuyTime.Date)))
                || data.Positions.Any(b=>b.Key.Account==p.Account && b.Quantity!=0 && (b.Date<p.Opened || p.Closed is {} closed && b.Date>=closed))
                || p.OpeningZeroConfirmed && p.Opened.Year==year && data.BeginningPositions.Any(b=>b.Key.Account==p.Account && b.Date<=p.Opened && b.Quantity!=0))
                throw new CrsException("开户／关闭日期或开户零持仓确认与原始交易、现金或成本事实矛盾。");
        }
    }
}
