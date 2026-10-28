using System.Text.Json;
using CRS.Common.Integrity;
namespace CRS.Plugins.NationalTeamWatch.Domain;

public sealed record NamedEtfHistoryInput(EtfKey Key,DateOnly AsOf,DateTimeOffset SnapshotCutoff,AlertFundMetadata? Metadata,IReadOnlyList<NamedEtfHolding> Holdings);
public sealed record NamedEtfHistoryExclusion(string HoldingId,string Reason);
public sealed record NamedEtfHistoryRow(DateOnly ReportAsOf,string LegalEntityId,string LegalName,string Account,string InstitutionGroup,
    string State,decimal? Shares,DateOnly? PreviousAsOf,decimal? PreviousShares,decimal? Delta,IReadOnlyList<string> EvidenceIds,string Detail);
public sealed record NamedEtfHistoryBaseline(DateOnly? ReportAsOf,string State,IReadOnlyList<NamedEtfHolding> Holdings,IReadOnlyList<NamedEtfHistoryExclusion> Exclusions,IReadOnlyList<string> Issues);
public sealed record NamedEtfFamilyCoverage(string Family,int LatestDisclosedAccounts,DateOnly? ReportAsOf,string State);
public sealed record NamedEtfHoldingHistoryReport(NamedEtfHistoryInput Input,IReadOnlyList<NamedEtfHistoryRow> Rows,NamedEtfHistoryBaseline Baseline,
    IReadOnlyList<NamedEtfFamilyCoverage> Families,string Fingerprint)
{
    public const string Version="named-etf-history-v1";
    public string FormatVersion {get;init;}=Version;
    public string CatalogVersion=>InstitutionAccounts.Version;
    public string Mode=>"CurrentSnapshotResearch";
    public bool WholePortfolioProven=>false;
    public string DailyTradingEvidence=>"NotObserved";
    public string HistoricalKnowledgeProof=>"NotProven";
    public string Basis=>"当前已核原件中的具名账户披露样本；同一证券、法人及账户按相邻已登记报告期比较。未列示不填零，不把份额差额当作当日成交或金额流向。名称目录仅作分类参考，不证明完整机构组合。";
}

/// <summary>保留每期及排除原因，最新期异常时不回退；最新有效披露子集不等于完整机构持仓。</summary>
public static class NamedEtfHoldingHistory
{
    private sealed record PeriodAccount(NamedEtfHolding Value,IReadOnlyList<string> Ids,string State)
    {public bool Healthy=>State=="ReviewedDisclosure";}
    public static NamedEtfHoldingHistoryReport Evaluate(NamedEtfHistoryInput request)
    {
        if(request.AsOf==default||request.SnapshotCutoff==default||request.AsOf>=DateOnly.FromDateTime(request.SnapshotCutoff.ToOffset(TimeSpan.FromHours(8)).DateTime)
            ||request.Key.Market is not ("SSE" or "SZSE")||request.Key.Ticker is not {Length:6}||request.Holdings.Count>4096)
            throw new InvalidDataException("具名ETF历史证券、统计日或资料数量无效。");
        if(request.Holdings.Any(x=>x.Market!=request.Key.Market||x.Ticker!=request.Key.Ticker||string.IsNullOrWhiteSpace(x.Id)||string.IsNullOrWhiteSpace(x.LegalEntityId)||string.IsNullOrWhiteSpace(x.Account))
            ||request.Holdings.Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()!=request.Holdings.Count)throw new InvalidDataException("具名ETF历史混入其他证券、重复修订或缺少账户身份。");
        var input=JsonSerializer.Deserialize<NamedEtfHistoryInput>(JsonSerializer.Serialize(request with{Holdings=request.Holdings.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray()}))!;
        var exclusions=new List<NamedEtfHistoryExclusion>();var available=new List<NamedEtfHolding>();
        foreach(var holding in input.Holdings)
        {
            var reason=holding.HoldingAsOf>input.AsOf?"ReportAfterAsOf":holding.PublishedAt>input.SnapshotCutoff||holding.FirstSeenAt>input.SnapshotCutoff?"KnownAfterCutoff":null;
            if(reason is null)available.Add(holding);else exclusions.Add(new(holding.Id,reason));
        }
        var periods=available.Select(x=>x.HoldingAsOf).Distinct().Order().ToArray();var rows=new List<NamedEtfHistoryRow>();
        Dictionary<(string Legal,string Account),PeriodAccount> prior=[];DateOnly? priorDate=null;
        Dictionary<(string Legal,string Account),PeriodAccount> latest=[];
        foreach(var period in periods)
        {
            var current=available.Where(x=>x.HoldingAsOf==period).GroupBy(x=>(LegalEntityId:x.LegalEntityId.Trim(),Account:x.Account.Trim())).ToDictionary(g=>g.Key,g=>Normalize(g.ToArray()));
            var ambiguousNames=current.Values.GroupBy(x=>(x.Value.LegalName.Trim(),x.Value.Account.Trim())).Where(g=>g.Select(x=>x.Value.LegalEntityId.Trim()).Distinct(StringComparer.Ordinal).Count()>1).Select(g=>g.Key).ToHashSet();
            var conflictingIds=current.Values.GroupBy(x=>x.Value.LegalEntityId.Trim()).Where(g=>g.Select(x=>x.Value.LegalName.Trim()).Distinct(StringComparer.Ordinal).Count()>1).Select(g=>g.Key).ToHashSet(StringComparer.Ordinal);
            foreach(var identity in current.Keys.ToArray())
                if(ambiguousNames.Contains((current[identity].Value.LegalName.Trim(),current[identity].Value.Account.Trim()))||conflictingIds.Contains(identity.LegalEntityId))current[identity]=current[identity] with{State="AmbiguousLegalIdentity"};
            foreach(var pair in current.OrderBy(x=>x.Key.LegalEntityId,StringComparer.Ordinal).ThenBy(x=>x.Key.Account,StringComparer.Ordinal))
            {
                var account=pair.Value;var holding=account.Value;prior.TryGetValue(pair.Key,out var previous);
                var state=!account.Healthy?account.State:previous is null?"FirstDisclosedPreviousUnknown":!previous.Healthy?"PreviousEvidenceUnavailable"
                    :holding.LegalName.Trim()!=previous.Value.LegalName.Trim()?"IdentityChanged":input.Metadata is {} metadata&&metadata.Market==input.Key.Market&&metadata.Ticker==input.Key.Ticker&&metadata.Covers(priorDate!.Value,period)?"Comparable":"NotComparable";
                decimal? delta=null;
                if(state=="Comparable")
                {
                    delta=holding.Shares-previous!.Value.Shares;state=delta>0?"Increased":delta<0?"Decreased":"Unchanged";
                }
                rows.Add(new(period,holding.LegalEntityId,holding.LegalName,holding.Account,InstitutionAccounts.Group(holding.LegalName)??"主体待核验",state,
                    account.Healthy?holding.Shares:null,previous is null?null:priorDate,previous?.Healthy==true?previous.Value.Shares:null,delta,
                    account.Ids.Concat(previous?.Ids??[]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),"比较仅限相邻已登记披露期和同一账户；原件修订不相加，未登记期间的变化未知。"));
            }
            foreach(var missing in prior.Where(x=>!current.ContainsKey(x.Key)).OrderBy(x=>x.Key.Legal,StringComparer.Ordinal).ThenBy(x=>x.Key.Account,StringComparer.Ordinal))
            {
                var value=missing.Value.Value;rows.Add(new(period,value.LegalEntityId,value.LegalName,value.Account,InstitutionAccounts.Group(value.LegalName)??"主体待核验",
                    "NotDisclosed",null,priorDate,missing.Value.Healthy?value.Shares:null,null,missing.Value.Ids,"本期已登记资料没有该账户，不代表零持仓、卖出或退出；不沿用旧期份额。"));
            }
            prior=current;priorDate=period;latest=current;
        }
        var latestPeriod=periods.Length==0?(DateOnly?)null:periods[^1];var issues=new List<string>();
        var unhealthy=latest.Values.Any(x=>!x.Healthy);if(unhealthy)issues.Add("最新报告期存在冲突或未核验原件，基准关闭，不回退到较早期。");
        var selected=unhealthy?[]:latest.Values.Select(x=>x.Value).Where(x=>InstitutionAccounts.Group(x.LegalName) is not null).OrderBy(x=>x.LegalEntityId,StringComparer.Ordinal).ThenBy(x=>x.Account,StringComparer.Ordinal).ToArray();
        var selectedIds=selected.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var holding in available.Where(x=>!selectedIds.Contains(x.Id)))
            exclusions.Add(new(holding.Id,holding.HoldingAsOf<latestPeriod?"OlderReportPeriod":unhealthy?"LatestPeriodUnavailable":InstitutionAccounts.Group(holding.LegalName) is null?"InstitutionIdentityUnresolved":"DuplicateSameFactEvidence"));
        if(latestPeriod is not null&&!unhealthy&&selected.Length==0)issues.Add("最新期没有目录可识别的已核主体，不能将其他具名持有者归因国家队，也不回退旧期。");
        var baseline=new NamedEtfHistoryBaseline(latestPeriod,latestPeriod is null?"NoDisclosedBaseline":unhealthy?"LatestBaselineUnavailable":selected.Length==0?"NoRecognizedInstitutionBaseline":"LatestReviewedDisclosedSubset",selected,exclusions.OrderBy(x=>x.HoldingId,StringComparer.Ordinal).ToArray(),issues);
        var families=InstitutionAccounts.RequiredFamilies.Select(family=>
        {
            var count=selected.Count(x=>InstitutionAccounts.Family(InstitutionAccounts.Group(x.LegalName)??"主体待核验")==family);
            return new NamedEtfFamilyCoverage(family,count,count==0?null:latestPeriod,count==0?"NoReviewedDisclosureInLatestSubset":"DisclosedSubsetOnly");
        }).ToArray();
        var orderedRows=rows.OrderByDescending(x=>x.ReportAsOf).ThenBy(x=>x.LegalEntityId,StringComparer.Ordinal).ThenBy(x=>x.Account,StringComparer.Ordinal).ToArray();
        var canonical=JsonSerializer.SerializeToUtf8Bytes(new{Version=NamedEtfHoldingHistoryReport.Version,Catalog=InstitutionAccounts.Version,Input=input,Rows=orderedRows,Baseline=baseline,Families=families});
        return new(input,orderedRows,baseline,families,ContentFingerprint.Sha256(canonical));
    }
    private static PeriodAccount Normalize(NamedEtfHolding[] values)
    {
        var selected=values.OrderByDescending(x=>x.FirstSeenAt).ThenBy(x=>x.Id,StringComparer.Ordinal).First();
        var conflict=values.Select(x=>(x.Shares,Name:x.LegalName.Trim())).Distinct().Count()!=1;
        var state=conflict?"ConflictingDisclosures":values.All(x=>x.HoldingAsOf!=default&&x.PublishedAt!=default&&x.FirstSeenAt!=default&&ExitAlertEvaluator.NamedVerified(x))?"ReviewedDisclosure":"EvidenceUnavailable";
        return new(selected,values.Select(x=>x.Id).Order(StringComparer.Ordinal).ToArray(),state);
    }
}
