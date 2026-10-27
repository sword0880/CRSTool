namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>以已登记披露原件为依据的人工作业；不通过证券名称推断行业、风格或价格。</summary>
public sealed record EquityDirectionReview(string DisclosureId,string DocumentHash,string Scheme,string SchemeVersion,
    string SectorCode,string SectorName,string? Style,string Page,string Reviewer,string Reason,bool OriginalConfirmed,
    decimal? ClosingPriceCny=null,DateOnly? PriceDate=null,string? PricePage=null,string? PreviousReviewId=null);

public sealed record EquityDirectionFact(string ReviewId,string DisclosureId,string DocumentHash,DateOnly ReportAsOf,
    string Scheme,string SchemeVersion,string SectorCode,string SectorName,string? Style,string Page,
    decimal? ClosingPriceCny,DateOnly? PriceDate,string? PricePage,DateTimeOffset ReviewedAt);

public sealed record EquityDirectionGap(string Market,string Ticker,string Name,string Code,string Detail)
{
    public string StatusText=>Code switch
    {
        "MissingClassification"=>"行业未核验","MissingPeriodPrice"=>"期末价缺失","ValueOverflow"=>"市值不可计算",
        "NoReviewedHoldingInPeriod"=>"本期快照缺失","MixedClassificationStandards"=>"分类口径不一致",
        "ConflictingClassificationNames"=>"行业代码含义冲突",
        "ConflictingDisclosures"=>"同报告期披露冲突","EvidenceUnavailable"=>"原件证据不可用",
        "UnreviewedAccounts"=>"部分账户未核验","ClassificationEvidenceUnavailable"=>"方向核验依据不可用",_=>"资料待核验"
    };
}
public sealed record EquityDirectionPosition(EquityHoldingRow Holding,EquityDirectionFact? Fact,decimal? MarketValueCny);
public sealed record EquityDirectionGroup(string Institution,string Nature,string Scheme,string SchemeVersion,
    string SectorCode,string SectorName,string Style,int Securities,int Accounts,decimal? MarketValueCny,decimal? SampleWeightPercent)
{
    public string NatureText=>Nature=="Strategic"?"战略控股/参股":Nature=="Portfolio"?"证券投资持仓":"性质待核验";
}

/// <summary>当前启用股票池的同报告期样本；不表示具名机构的完整组合或每日交易。</summary>
public sealed record EquityDirectionReport(DateOnly? ReportAsOf,DateTimeOffset ObservedAt,int ActiveSecurities,
    int DisclosedSecurities,int ReviewedAccounts,int ClassifiedSecurities,int PricedSecurities,
    IReadOnlyList<EquityDirectionPosition> Positions,IReadOnlyList<EquityDirectionGroup> Groups,IReadOnlyList<EquityDirectionGap> Gaps)
{
    public const string Version="equity-direction-v1";
    public string FormatVersion {get;init;}=Version;
    public string Scope=>"CurrentActiveWatchlistReviewedDirectAShares";
    public bool WholePortfolioProven=>false;
    public string Denominator=>"同报告期已核具名A股样本；统一分类标准版本下，按机构和持仓性质分别计算，未覆盖持仓不计为零。";
}

public static class EquityDirectionProjection
{
    public static EquityDirectionReport Build(DateOnly? period,DateTimeOffset observedAt,IReadOnlyList<EquityWatch> watches,
        IReadOnlyList<EquityHoldingRow> holdings,IReadOnlyList<EquityDirectionFact> facts,IReadOnlyList<EquityDirectionGap> sourceGaps)
    {
        var active=watches.Where(x=>x.Active).ToDictionary(x=>new EtfKey(x.Market,x.Ticker));
        var rows=holdings.Where(x=>x.ReportAsOf==period&&period is not null&&active.ContainsKey(new(x.Market,x.Ticker))
            &&x.Published is {} publication&&publication<=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Read(new FixedClock(observedAt)).DateTime)
            &&x.EvidenceLevel==EvidenceLevel.DisclosedHolding).ToArray();
        if(rows.GroupBy(x=>(x.Market,x.Ticker,x.Account)).Any(x=>x.Count()>1))
            throw new InvalidDataException("同报告期同证券账户出现重复披露，拒绝重复汇总。");
        var gaps=sourceGaps.ToList();var positions=new List<EquityDirectionPosition>();
        var evidenceBlocked=sourceGaps.Any(x=>x.Code is "EvidenceUnavailable" or "ConflictingDisclosures" or "UnreviewedAccounts");
        foreach(var row in rows)
        {
            var applicable=facts.Where(x=>x.DisclosureId==row.DisclosureId&&x.ReportAsOf==period
                &&x.DocumentHash.Equals(row.EvidenceHash,StringComparison.OrdinalIgnoreCase)&&x.ReviewedAt<=observedAt).ToArray();
            if(applicable.Length>1)throw new InvalidDataException("行业方向核验版本不唯一。");
            var fact=applicable.SingleOrDefault();decimal? value=null;
            if(fact is null)gaps.Add(new(row.Market,row.Ticker,row.Name,"MissingClassification","该报告期尚无绑定原文的行业核验。"));
            else if(fact.ClosingPriceCny is >0&&fact.PriceDate==period&&row.Shares is >=0)
            {try{value=checked(row.Shares.Value*fact.ClosingPriceCny.Value);}catch(OverflowException){gaps.Add(new(row.Market,row.Ticker,row.Name,"ValueOverflow","市值超出可计算范围。"));}}
            if(value is null)gaps.Add(new(row.Market,row.Ticker,row.Name,"MissingPeriodPrice","缺少该报告截止日的A股未复权人民币收盘价核验；不按跨证券股数计算权重。"));
            positions.Add(new(row,fact,value));
        }
        foreach(var watch in active.Values.Where(w=>!rows.Any(x=>x.Market==w.Market&&x.Ticker==w.Ticker)))
            gaps.Add(new(watch.Market,watch.Ticker,watch.Name,"NoReviewedHoldingInPeriod","本报告期没有已核具名账户快照；不能解释为未持有。"));
        var groups=new List<EquityDirectionGroup>();
        // 每种性质及分类体系独立分母，战略持股、证券投资与未知目的不能混为一个配置权重。
        foreach(var cohort in positions.GroupBy(x=>(x.Holding.Institution,x.Holding.Nature)))
        {
            var standards=cohort.Where(x=>x.Fact is not null).Select(x=>(x.Fact!.Scheme,x.Fact.SchemeVersion)).Distinct().ToArray();
            if(standards.Length>1)foreach(var position in cohort)gaps.Add(new(position.Holding.Market,position.Holding.Ticker,position.Holding.Name,"MixedClassificationStandards","同机构同性质样本使用不同分类标准或版本，权重暂不可计算。"));
            var namesConflict=cohort.Where(x=>x.Fact is not null).GroupBy(x=>(x.Fact!.Scheme,x.Fact.SchemeVersion,x.Fact.SectorCode)).Any(x=>x.Select(v=>v.Fact!.SectorName).Distinct(StringComparer.Ordinal).Count()>1);
            if(namesConflict)foreach(var position in cohort)gaps.Add(new(position.Holding.Market,position.Holding.Ticker,position.Holding.Name,"ConflictingClassificationNames","同一分类标准版本中的相同行业代码对应不同名称，请核对原文和更正记录。"));
            decimal? denominator=null;
            if(!evidenceBlocked&&!namesConflict&&standards.Length==1&&cohort.All(x=>x.Fact is not null&&x.MarketValueCny.HasValue))
            {try{denominator=cohort.Sum(x=>x.MarketValueCny!.Value);}catch(OverflowException){denominator=null;}}
            foreach(var group in cohort.GroupBy(x=>(Scheme:x.Fact?.Scheme??"未归类",Version:x.Fact?.SchemeVersion??"",Code:x.Fact?.SectorCode??"",Name:x.Fact?.SectorName??"未归类",Style:x.Fact?.Style??"风格未核验")))
            {
                decimal? total=null;try{if(group.All(x=>x.MarketValueCny.HasValue))total=group.Sum(x=>x.MarketValueCny!.Value);}catch(OverflowException){/* 缺失保持不可计算。 */}
                groups.Add(new(cohort.Key.Institution,cohort.Key.Nature,group.Key.Scheme,group.Key.Version,group.Key.Code,group.Key.Name,group.Key.Style,
                    group.Select(x=>(x.Holding.Market,x.Holding.Ticker)).Distinct().Count(),group.Count(),total,
                    denominator is >0&&total.HasValue?total.Value/denominator.Value*100m:null));
            }
        }
        return new(period,observedAt,active.Count,rows.Select(x=>(x.Market,x.Ticker)).Distinct().Count(),rows.Length,
            positions.Where(x=>x.Fact is not null).Select(x=>(x.Holding.Market,x.Holding.Ticker)).Distinct().Count(),
            positions.Where(x=>x.MarketValueCny.HasValue).Select(x=>(x.Holding.Market,x.Holding.Ticker)).Distinct().Count(),positions,
            groups.OrderBy(x=>x.Institution,StringComparer.Ordinal).ThenBy(x=>x.Nature,StringComparer.Ordinal).ThenBy(x=>x.Scheme,StringComparer.Ordinal).ThenBy(x=>x.SectorCode,StringComparer.Ordinal).ThenBy(x=>x.Style,StringComparer.Ordinal).ToArray(),
            gaps.Distinct().OrderBy(x=>x.Market,StringComparer.Ordinal).ThenBy(x=>x.Ticker,StringComparer.Ordinal).ThenBy(x=>x.Code,StringComparer.Ordinal).ToArray());
    }
    private sealed class FixedClock(DateTimeOffset now):TimeProvider{public override DateTimeOffset GetUtcNow()=>now;}
}
