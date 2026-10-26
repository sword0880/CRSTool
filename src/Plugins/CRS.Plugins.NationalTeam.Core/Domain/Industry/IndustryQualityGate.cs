using System.Security.Cryptography;
using System.Text.Json;

namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>不同经营模式需要不同反证指标。政策是研究完整性要求，不是跨行业打分模型。</summary>
public enum IndustryQualityProfile { Financial, Cyclical, Manufacturing, Innovation, Platform, General }
public enum IndustryQualityMeasure
{
    ReturnOnEquity, AssetQualityRisk, CapitalAdequacy,
    MidCycleEarnings, CashConversion, BalanceSheetLeverage,
    ReturnOnInvestedCapital, FreeCashFlowMargin, ResearchFundingCoverage,
    CashRunwayMonths, RevenueRetention
}
/// <summary>指标单位固定，防止将8%误当8或0.08；Ratio统一采用0.08表示8%。</summary>
public enum IndustryQualityUnit { Ratio, Multiple, Months }

/// <summary>独立基本面资料必须有来源身份、时间、公司池与方法。ResearchUseAllowed只能由未来可信来源准入层填入。</summary>
public sealed record IndustryQualityObservation(IndustryQualityMeasure Measure,decimal? Value,IndustryQualityUnit Unit,DateOnly PeriodEnd,
    decimal? MarketCapCoverage,string Method,string UniverseDefinition,string UniverseSnapshotHash,string SourceId,
    bool ResearchUseAllowed,bool DerivedFromValuation,SectorEvidence Evidence);
public sealed record IndustryQualityInput(string SectorId,string Scheme,string SchemeVersion,IndustryQualityProfile Profile,
    IReadOnlyList<IndustryQualityObservation> Observations)
{
    // 手工/来源规则赋予的经营模式分类须单独留痕；没有核验来源不能自行选择最宽松的Profile。
    public string ProfileRuleSet {get;init;}="";
    public SectorEvidence? ProfileMappingEvidence {get;init;}
}
public sealed record IndustryQualityMetricReview(IndustryQualityMeasure Measure,string State,string Explanation);

/// <summary>Ready仅表示证据齐备可人工反方复核，绝不代表行业盈利质量优秀或已低估。</summary>
public sealed record IndustryQualityGateReport(string SectorId,IndustryQualityProfile? Profile,string State,
    IReadOnlyList<IndustryQualityMetricReview> Metrics,string Fingerprint)
{
    public string KnowledgeMode=>"CurrentKnowledgeResearch";
    public string ValuationProof=>"NotProven";
    public string InvestmentConclusion=>"NotEvaluated";
}

public static class IndustryQualityGate
{
    public const string AlgorithmVersion="industry-quality-readiness-v1";
    public const decimal MinimumMarketCapCoverage=0.8m;
    public const int MaxAgeDays=180;

    private static readonly IReadOnlyDictionary<IndustryQualityProfile,IndustryQualityMeasure[]> Requirements=
        new Dictionary<IndustryQualityProfile,IndustryQualityMeasure[]>
        {
            [IndustryQualityProfile.Financial]=[IndustryQualityMeasure.ReturnOnEquity,IndustryQualityMeasure.AssetQualityRisk,IndustryQualityMeasure.CapitalAdequacy],
            [IndustryQualityProfile.Cyclical]=[IndustryQualityMeasure.MidCycleEarnings,IndustryQualityMeasure.CashConversion,IndustryQualityMeasure.BalanceSheetLeverage],
            [IndustryQualityProfile.Manufacturing]=[IndustryQualityMeasure.ReturnOnInvestedCapital,IndustryQualityMeasure.CashConversion,IndustryQualityMeasure.BalanceSheetLeverage],
            [IndustryQualityProfile.Innovation]=[IndustryQualityMeasure.ResearchFundingCoverage,IndustryQualityMeasure.CashRunwayMonths,IndustryQualityMeasure.FreeCashFlowMargin],
            [IndustryQualityProfile.Platform]=[IndustryQualityMeasure.FreeCashFlowMargin,IndustryQualityMeasure.ReturnOnInvestedCapital,IndustryQualityMeasure.RevenueRetention],
            [IndustryQualityProfile.General]=[IndustryQualityMeasure.ReturnOnInvestedCapital,IndustryQualityMeasure.FreeCashFlowMargin,IndustryQualityMeasure.BalanceSheetLeverage]
        };

    public static IndustryQualityGateReport Evaluate(SectorDefinition definition,IndustryQualityInput? input,
        DateOnly asOf,DateTimeOffset cutoff,string? currentUniverseHash)
    {
        if(asOf<definition.ValidFrom||asOf>definition.ValidThrough||cutoff==default
            ||asOf>DateOnly.FromDateTime(cutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))
            throw new InvalidDataException("基本面审核日与行业有效期或知识时点冲突。");
        var observations=input?.Observations?.ToArray()??[];
        if(observations.Length>100||observations.Select(x=>x.Measure).Distinct().Count()!=observations.Length)
            throw new InvalidDataException("基本面指标重复或超过单行业配额，不得择优。");
        // 完整冻结全部输入，不能让调用方在校验及指纹期间改变底层列表。
        input=input is null?null:input with{Observations=Array.AsReadOnly(observations)};
        var frozen=new{Version=AlgorithmVersion,definition,asOf,Cutoff=cutoff.ToUniversalTime(),currentUniverseHash,
            Profile=input?.Profile,InputSector=input?.SectorId,InputScheme=input?.Scheme,InputVersion=input?.SchemeVersion,
            ProfileRuleSet=input?.ProfileRuleSet,ProfileMappingEvidence=input?.ProfileMappingEvidence,
            Observations=observations.OrderBy(x=>x.Measure).Select(x=>x with
            {
                Evidence=x.Evidence with{FirstSeenAt=x.Evidence.FirstSeenAt.ToUniversalTime(),
                    RetrievedAt=x.Evidence.RetrievedAt.ToUniversalTime(),AcceptedAt=x.Evidence.AcceptedAt.ToUniversalTime(),
                    PublishedAt=x.Evidence.PublishedAt?.ToUniversalTime()}
            }).ToArray()};
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(frozen)));
        if(input is null)return new(definition.Id,null,"MissingQualitySource",[],fingerprint);
        if(input.SectorId!=definition.Id||input.Scheme!=definition.Scheme||input.SchemeVersion!=definition.SchemeVersion
            ||!Enum.IsDefined(input.Profile)||string.IsNullOrWhiteSpace(currentUniverseHash)
            ||!ValidHash(currentUniverseHash))
            return new(definition.Id,input.Profile,"IncompatibleUniverseOrClassification",[],fingerprint);

        var assignment=input.ProfileMappingEvidence;
        if(input.ProfileRuleSet!="industry-profile-v1"||assignment is null
            ||assignment.State!="Accepted"||string.IsNullOrWhiteSpace(assignment.RevisionId)
            ||!ValidHash(assignment.Hash)||assignment.PublishedAt is not {} profilePublished
            ||profilePublished>assignment.FirstSeenAt||assignment.RetrievedAt==default
            ||assignment.RetrievedAt>assignment.FirstSeenAt||assignment.FirstSeenAt>assignment.AcceptedAt
            ||assignment.AcceptedAt>cutoff)
            return new(definition.Id,input.Profile,"UnverifiedProfileMapping",[],fingerprint);
        var checks=new List<IndustryQualityMetricReview>();
        foreach(var measure in Requirements[input.Profile])
        {
            var fact=observations.SingleOrDefault(x=>x.Measure==measure);
            if(fact is null){checks.Add(new(measure,"Missing","没有独立的已审核基本面数据。"));continue;}
            var source=fact.Evidence;
            var expectedUnit=measure==IndustryQualityMeasure.CashRunwayMonths?IndustryQualityUnit.Months
                :measure==IndustryQualityMeasure.BalanceSheetLeverage?IndustryQualityUnit.Multiple:IndustryQualityUnit.Ratio;
            var plausible=fact.Value.HasValue&&(expectedUnit switch
            {
                IndustryQualityUnit.Months=>fact.Value is >=0m and <=600m,
                IndustryQualityUnit.Multiple=>fact.Value is >=-10m and <=100m,
                _=>fact.Value is >=-5m and <=10m
            });
            var healthy=plausible&&fact.Unit==expectedUnit
                &&fact.MarketCapCoverage is >=MinimumMarketCapCoverage and <=1m
                &&fact.PeriodEnd<=asOf&&fact.PeriodEnd>=asOf.AddDays(-MaxAgeDays)
                &&!string.IsNullOrWhiteSpace(fact.Method)&&!string.IsNullOrWhiteSpace(fact.SourceId)
                &&fact.UniverseDefinition==definition.UniverseDefinition
                &&fact.UniverseSnapshotHash==currentUniverseHash
                &&fact.ResearchUseAllowed&&!fact.DerivedFromValuation
                &&source.State=="Accepted"&&!string.IsNullOrWhiteSpace(source.RevisionId)
                &&ValidHash(source.Hash)&&source.RetrievedAt!=default
                &&source.RetrievedAt<=source.FirstSeenAt&&source.FirstSeenAt<=source.AcceptedAt&&source.AcceptedAt<=cutoff
                &&source.PublishedAt is {} published&&published>=fact.PeriodEnd.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)
                &&published<=source.FirstSeenAt&&published<=cutoff;
            // 基本面必须来自可审查原件，不能拿走势图、低PE或同一数值的人工补录充作资料。
            checks.Add(new(measure,healthy?"EvidencePresent":"Unverified","数据未满足来源、许可、期间、单位、市值覆盖或公司池一致性。"));
        }
        return new(definition.Id,input.Profile,checks.All(x=>x.State=="EvidencePresent")
            ?"ReadyForAdversarialReview":"IncompleteQualityEvidence",checks.AsReadOnly(),fingerprint);
    }

    private static bool ValidHash(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
}
