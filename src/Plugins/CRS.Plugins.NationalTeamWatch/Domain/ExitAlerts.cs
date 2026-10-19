using System.Globalization;
using System.Security.Cryptography;
using System.Text;
namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>严重度描述市场现象，证据类型独立描述能够证明什么，两者不能互相升级。</summary>
public enum AlertSeverity { Info,Watch,Warning,Critical }
public enum AlertEvidenceKind { OfficialMarketObserved,PendingQualityReview,NamedHoldingVerified,DerivedMinimumReduction,ResearchHypothesis,InsufficientEvidence }
public enum AlertLane { DataQuality,EtfOutflow,InstitutionEvidence }

/// <summary>候选阈值保留不可变版本；默认研究预览，正式自动外发还须通过真实基础数据门槛。</summary>
public sealed record ExitAlertRules(string Version="exit-v1.0",bool Enabled=true,decimal Decline5=0.05m,decimal Decline20=0.10m,
    int MinimumFunds=3,int MinimumManagers=2,int ClearObservations=2,bool NotifyWatch=false,bool NotifyResolved=true)
{
    public ExitAlertRules Validate()
    {
        if(Version is null||Version.Length is <1 or >40||Version.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '.' and not '-')
            ||Decline5 is <=0 or >=1||Decline20 is <=0 or >=1||MinimumFunds is <2 or >100||MinimumManagers is <2 or >30||ClearObservations is <2 or >20)
            throw new InvalidDataException("预警规则版本或候选阈值无效。");
        return this;
    }
}

/// <summary>份额可比性单独审核，管理人和基金标识不从名称相似度推断；缺少原文时不能自动视作已核验。</summary>
public sealed record AlertFundMetadata(string Market,string Ticker,string FundId,string ManagerId,string IndexId,
    DateOnly ComparableFrom,DateOnly ComparableThrough,string EvidenceUrl,string EvidenceHash,string Reviewer,bool OriginalConfirmed)
{
    public bool Covers(DateOnly from,DateOnly through)=>OriginalConfirmed&&FundId.Length>0&&ManagerId.Length>0&&IndexId.Length>0
        &&ComparableFrom<=from&&ComparableThrough>=through&&EvidenceHash.Length==64&&EvidenceHash.All(Uri.IsHexDigit)
        &&!string.IsNullOrWhiteSpace(Reviewer)&&Uri.TryCreate(EvidenceUrl,UriKind.Absolute,out var uri)&&OfficialEquityDocuments.IsAllowed(uri)&&!OfficialEquityDocuments.IsReviewedIssuer(uri);
}

/// <summary>输入仅为已接受且原件健康的份额；缺口、交易日历及份额折算可比性均由后台保留。</summary>
public sealed record AlertFundInput(EtfKey Key,string Name,IReadOnlyList<DateOnly> TradingDays,IReadOnlyList<SharePoint> Points,
    AlertFundMetadata? Metadata,bool CalendarVerified,DateTimeOffset EvaluatedAt);
public sealed record AlertWindows(DateOnly Date,string State,decimal? Pct1,decimal? Pct5,decimal? Pct20,
    IReadOnlyList<string> RevisionIds,string Fingerprint,bool Triggered)
{
    public bool Complete=>State=="Complete";
}
public sealed record ExitAlertFinding(string RuleId,string ScopeId,AlertLane Lane,AlertSeverity Severity,AlertEvidenceKind EvidenceKind,
    DateOnly ObservationDate,string Headline,string Explanation,string Fingerprint,IReadOnlyList<string> RevisionIds,
    bool Triggered,bool Complete,bool Preview,decimal? MinimumReduction=null);

/// <summary>具名机构的ETF持仓，与股票直接持股分别存储；首见时间由系统记录，匿名名称不能据数额匹配身份。</summary>
public sealed record NamedEtfHolding(string Id,string Market,string Ticker,string LegalEntityId,string LegalName,string Account,
    DateOnly HoldingAsOf,DateTimeOffset PublishedAt,DateTimeOffset FirstSeenAt,decimal Shares,string DocumentUrl,string DocumentHash,
    string EvidencePage,string Reviewer,bool OriginalConfirmed,bool IndependentOwnershipConfirmed);
public sealed record MinimumReductionProof(string State,decimal? BaselineShares,decimal? CurrentShares,decimal? LowerBound,
    DateOnly? BaselineDate,IReadOnlyList<string> LegalEntities,IReadOnlyList<string> EvidenceIds,string Explanation);

/// <summary>纯份额规则，不请求网络、不生成金额、不将ETF全市场变化归因到具体机构。</summary>
public static class ExitAlertEvaluator
{
    public static AlertWindows Windows(AlertFundInput input,DateOnly date,ExitAlertRules rules)
    {
        rules.Validate();var days=input.TradingDays.Distinct().Order().ToArray();var index=Array.IndexOf(days,date);
        if(!input.CalendarVerified||index<1)return Empty(date,"IncompleteCalendar");
        var points=input.Points.ToDictionary(x=>x.Date);var ids=new HashSet<string>(StringComparer.Ordinal);
        decimal? Pct(int window)
        {
            // 各窗口独立检查；缺20日不伪造20日，也不抹去已经完整的5日信号。
            if(index<window||input.Metadata?.Covers(days[index-window],date)!=true)return null;
            var required=days[(index-window)..(index+1)];
            if(required.Any(x=>!points.ContainsKey(x)||points[x].Shares<=0))return null;
            foreach(var day in required)ids.Add(points[day].RevisionId);
            return points[date].Shares/points[required[0]].Shares-1m;
        }
        var pct1=Pct(1);var pct5=Pct(5);var pct20=Pct(20);var revisions=ids.Order().ToArray();
        var state=pct5.HasValue&&pct20.HasValue?"Complete":pct5.HasValue||pct20.HasValue?"Partial":input.Metadata is null?"NotComparable":"IncompleteFactsOrComparability";
        return new(date,state,pct1,pct5,pct20,revisions,Hash(rules.Version+"|"+string.Join('|',revisions)+"|"+System.Text.Json.JsonSerializer.Serialize(input.Metadata)),pct5<=-rules.Decline5||pct20<=-rules.Decline20);
    }
    public static ExitAlertFinding Single(AlertFundInput input,DateOnly date,ExitAlertRules rules,bool acceptancePassed)
    {
        var value=Windows(input,date,rules);var scope=input.Key.Market+":"+input.Key.Ticker;
        var explanation=value.Pct5.HasValue||value.Pct20.HasValue?$"最近5个有效交易日份额变化{Percent(value.Pct5)}，20日{Percent(value.Pct20)}。仅为ETF全市场份额；不能据此确认任何机构的买卖。"
            :value.State=="NotComparable"?"份额折算及同口径证据尚未核验，不能生成正式窗口信号。":"日历或相邻交易日份额不完整，风险状态未知；不补零、不跨缺口计算。";
        return new("EtfShareContraction",scope,AlertLane.EtfOutflow,AlertSeverity.Watch,AlertEvidenceKind.OfficialMarketObserved,date,
            input.Name+" · ETF份额收缩观察",explanation,value.Fingerprint,value.RevisionIds,value.Triggered,value.Complete,!acceptancePassed);
    }
    public static ExitAlertFinding Cluster(IReadOnlyList<(AlertFundInput Input,AlertWindows Window)> inputs,DateOnly date,ExitAlertRules rules,bool acceptancePassed,int window=5)
    {
        if(window is not (5 or 20))throw new ArgumentOutOfRangeException(nameof(window));
        decimal? Value(AlertWindows value)=>window==5?value.Pct5:value.Pct20;
        var threshold=window==5?rules.Decline5:rules.Decline20;
        // 同一基金的不同份额类别只能计一次，不相加不同ETF的份额数。
        var complete=inputs.Count>0&&inputs.All(x=>x.Window.Date==date&&Value(x.Window).HasValue&&x.Input.Metadata is not null);
        var selected=inputs.Where(x=>Value(x.Window)<=-threshold&&x.Input.Metadata is not null)
            .GroupBy(x=>x.Input.Metadata!.FundId,StringComparer.Ordinal).Select(x=>x.First()).ToArray();
        var managers=selected.Select(x=>x.Input.Metadata!.ManagerId).Distinct(StringComparer.Ordinal).Count();
        var triggered=complete&&selected.Length>=rules.MinimumFunds&&managers>=rules.MinimumManagers;
        var ids=selected.SelectMany(x=>x.Window.RevisionIds).Distinct().Order().ToArray();
        return new("SampleBreadth","monitored-sample:"+window,AlertLane.EtfOutflow,AlertSeverity.Warning,AlertEvidenceKind.OfficialMarketObserved,date,
            "当前监控样本池 · 多基金份额同步收缩",$"达到候选阈值的独立基金{selected.Length}只、管理人{managers}家。样本池不代表全市场，也不能确认国家队身份；承接行情证据不足时不升级Critical。",
            Hash(rules.Version+"|"+string.Join('|',inputs.Select(x=>x.Window.Fingerprint).Order())),ids,triggered,complete,!acceptancePassed);
    }
    public static MinimumReductionProof Minimum(IReadOnlyList<NamedEtfHolding> holdings,AlertFundInput input,DateOnly date,DateTimeOffset cutoff)
    {
        var current=input.Points.SingleOrDefault(x=>x.Date==date);
        if(holdings.Count==0||current is null)return NoProof("缺少已审核具名ETF持仓或当前全市场份额。");
        if(holdings.Any(x=>x.Market!=input.Key.Market||x.Ticker!=input.Key.Ticker||x.HoldingAsOf>date||x.PublishedAt>cutoff||x.FirstSeenAt>cutoff))
            return NoProof("证券或时序不一致；不使用截止时点之后公布或首次取得的证据。");
        if(holdings.Select(x=>x.HoldingAsOf).Distinct().Count()!=1||holdings.Select(x=>x.LegalEntityId+"|"+x.Account).Distinct().Count()!=holdings.Count
            ||holdings.Any(x=>!NamedVerified(x))||input.Metadata?.Covers(holdings[0].HoldingAsOf,date)!=true)
            return NoProof("具名身份、独立持有关系、基准日或可比份额证据不足。");
        var total=holdings.Sum(x=>x.Shares);var lower=Math.Max(0,total-current.Shares);
        return new("DerivedMinimumReduction",total,current.Shares,lower,holdings[0].HoldingAsOf,
            holdings.Select(x=>x.LegalEntityId+" · "+x.LegalName).Distinct().ToArray(),holdings.Select(x=>x.Id).Append(current.RevisionId).ToArray(),
            "下界=max(0,具名机构基准持有份额之和－随后全市场总份额)。仅证明相对该披露基准日至少净减少，不代表单日成交、全部减持量或清仓。");
    }
    public static bool NamedVerified(NamedEtfHolding value)=>value.OriginalConfirmed&&value.IndependentOwnershipConfirmed&&value.Shares>0
        &&value.LegalEntityId.Length>0&&value.LegalName.Length>0&&!System.Text.RegularExpressions.Regex.IsMatch(value.LegalName,@"匿名|未知|机构\s*[一二三四五六七八九十0-9]|institution\s*\d",System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        &&value.DocumentHash.Length==64&&value.DocumentHash.All(Uri.IsHexDigit)&&!string.IsNullOrWhiteSpace(value.EvidencePage)
        &&!string.IsNullOrWhiteSpace(value.Reviewer)&&Uri.TryCreate(value.DocumentUrl,UriKind.Absolute,out var uri)&&OfficialEquityDocuments.IsAllowed(uri);
    private static AlertWindows Empty(DateOnly date,string state)=>new(date,state,null,null,null,[],"",false);
    private static string Percent(decimal? value)=>value.HasValue?value.Value.ToString("P2",CultureInfo.InvariantCulture):"不可计算";
    private static MinimumReductionProof NoProof(string reason)=>new("InsufficientEvidence",null,null,null,null,[],[],reason);
    public static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
