namespace CRS.Domain.Performance;

internal static class PerformanceInputValidator
{
    private static bool Currency(string value)=>value is not null && value.Length==3 && value.All(c=>c is >= 'A' and <= 'Z');
    internal static void Validate(PerformanceData data,PerformanceRequest request,CancellationToken cancellation)
    {
        if(data.Version!=1 || request.From>=request.To || request.From.Year<2000 || request.To.Year>2100 || request.AccountIds.Length is <1 or >100
            || request.AccountIds.Distinct().Count()!=request.AccountIds.Length || !Currency(request.Currency) || !Enum.IsDefined(request.Method)
            || request.PriceFallbackDays is <0 or >7 || request.AbsoluteTolerance is <0 or >100000 || request.RelativeTolerance is <0 or >0.01m || request.CurrencyUnit is <=0 or >1)
            throw new CrsException("绩效区间、范围、币种或容差无效。");
        if(data.Accounts.Length>100 || data.Events.Length>20000 || data.Valuations.Length>20000 || data.Prices.Length>50000 || data.Fx.Length>50000 || data.Coverage.Length>10000 || data.Transfers.Length>10000 || data.Revisions.Length>20000)
            throw new CrsException("绩效资料超过单次可处理限额。");
        if(data.Accounts.GroupBy(a=>a.Id).Any(g=>g.Count()>1) || data.Events.GroupBy(e=>e.Id).Any(g=>g.Count()>1)
            || data.Valuations.GroupBy(v=>v.Id).Any(g=>g.Count()>1) || data.Fx.GroupBy(f=>f.Id).Any(g=>g.Count()>1) || data.Prices.GroupBy(p=>p.Id).Any(g=>g.Count()>1)
            || request.AccountIds.Any(id=>!data.Accounts.Any(a=>a.Id==id))) throw new CrsException("绩效资料存在重复身份或账户不存在。");
        if(data.Accounts.Any(a=>string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Alias) || a.Alias.Length>100 || !Currency(a.BaseCurrency))
            || data.Events.Any(e=>string.IsNullOrWhiteSpace(e.Id) || !Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Quality) || !Currency(e.Currency) || e.Date!=DateOnly.FromDateTime(e.Time.Date) || Math.Abs(e.Amount)>1_000_000_000_000_000m || e.Fees<0 || e.Withholding<0 || e.Source is null)
            || data.Valuations.Any(v=>string.IsNullOrWhiteSpace(v.Id) || v.Date!=DateOnly.FromDateTime(v.AsOf.Date) || v.Components.Length is <1 or >10000 || !Enum.IsDefined(v.Quality) || !Enum.IsDefined(v.Moment) || v.Source is null || v.Components.Any(c=>!Currency(c.Currency) || c.Kind is not ("Security" or "Cash" or "Receivable" or "Liability" or "Payable") || c.Quantity<0 || c.Kind is "Liability" or "Payable" && c.Amount<0))
            || data.Fx.Any(f=>f.Rate is <=0 or >1000000 || !Currency(f.From) || !Currency(f.To) || f.Source is null || !Enum.IsDefined(f.Quality))
            || data.Prices.Any(p=>p.Price<0 || !Currency(p.Currency) || p.Source is null || !Enum.IsDefined(p.Quality))
            || data.Coverage.Any(c=>c.From>c.To || c.Source is null || c.ExplainedProfit.HasValue && !Currency(c.Currency))
            || data.Transfers.Any(l=>l.DifferenceTolerance<0 || !Currency(l.ToleranceCurrency))) throw new CrsException("绩效金额、事件或估值结构无效。");
        foreach(var e in data.Events)
        {
            cancellation.ThrowIfCancellationRequested();
            PerformanceClassifications.ValidateDirection(e);
        }
    }
}
