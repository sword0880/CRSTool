using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>分量采用各模块已核验的输出及其指纹；份额与NAV同源，不相加计为独立证据分。</summary>
public sealed record JointExitNavContext(EtfKey Key,DateTimeOffset KnowledgeCutoff,EtfNavStudyWindow? Window5,EtfNavStudyWindow? Window20,string Fingerprint);
public sealed record JointExitDirectionContext(DateOnly? Period,DateTimeOffset ObservedAt,int ActiveSecurities,int DisclosedSecurities,int ClassifiedSecurities,int PricedSecurities,
    int Gaps,int UnknownNatures,IReadOnlyList<EquityDirectionGroup> Groups,IReadOnlyList<string> OriginalHashes,string Fingerprint);
public sealed record JointExitIndustryContext(DateOnly Through,DateTimeOffset Cutoff,bool CalendarVerified,IReadOnlyList<IndustryPeSeries> Sectors,string Fingerprint);
public sealed record JointExitIssue(string Component,string Code,string Detail);
public sealed record JointExitInput(EtfConfiguration Configuration,DateOnly AsOfDate,DateTimeOffset SnapshotCutoff,
    ExitAlertRules ShareRules,AlertFundInput? Fund,IReadOnlyList<NamedEtfHolding> Holdings,EtfAcceptance? Acceptance,
    MarketCycleAssessment? Cycle,string? CycleReportFingerprint,JointExitNavContext? Nav,
    JointExitDirectionContext? Direction,JointExitIndustryContext? Industry,IReadOnlyList<JointExitIssue> Issues);
public sealed record JointExitRules(string Version="joint-exit-candidate-v1.1",int MaximumDisclosureAgeDays=370)
{
    public JointExitRules Validate()
    {
        if(Version!="joint-exit-candidate-v1.1"||MaximumDisclosureAgeDays is <1 or >730)throw new InvalidDataException("联合观察规则版本或披露时效过滤参数无效。");return this;
    }
}
public sealed record JointExitComponent(string Key,string Title,bool Ready,DateOnly? DataDate,string State,string Detail,IReadOnlyList<string> EvidenceReferences);
public sealed record JointExitStudyReport(JointExitInput Input,JointExitRules Rules,string State,bool DataReady,
    IReadOnlyList<JointExitComponent> Components,IReadOnlyList<string> SupportingEvidence,IReadOnlyList<string> CounterEvidence,
    IReadOnlyList<string> BlockingReasons,MinimumReductionProof MinimumReduction,string Fingerprint)
{
    public const string Version="joint-exit-study-v2";
    public string FormatVersion {get;init;}=Version;
    public string Mode=>"CurrentSnapshotResearch";
    public string TimingProof=>"NotProven";
    public string ModelValidation=>"NotValidated";
    public bool NotificationsEnabled=>false;
    public string InstitutionDailyTrading=>"NotObserved";
    public string Basis=>"当前配置池单只ETF的证据共现研究；不相加不同基金份额，不把NAV估计与份额变化重复计分，不证明国家队当日交易或已退出。";
    public string StateText=>State switch{"ReviewCandidate"=>"联合观察候选，需核查","NoJointCandidate"=>"本次未形成联合候选",_=>"证据不足，无法联合判断"};
}

/// <summary>可复算的证据共现规则，不输出概率或调用通知；字段齐全也不代表择时验证。</summary>
public static class JointExitStudy
{
    public static JointExitStudyReport Evaluate(JointExitInput input,JointExitRules? rules=null)
    {
        rules=(rules??new()).Validate();var key=new EtfKey(input.Configuration.Market,input.Configuration.Ticker);
        if(!input.Configuration.Active||input.SnapshotCutoff==default||input.AsOfDate==default||input.AsOfDate>=DateOnly.FromDateTime(input.SnapshotCutoff.ToOffset(TimeSpan.FromHours(8)).DateTime))throw new InvalidDataException("联合观察证券或统计日无效，只支持当前配置及已结束日期。");
        var components=new List<JointExitComponent>();var support=new List<string>();var counter=new List<string>();var blocks=input.Issues.Select(x=>x.Component+"："+x.Detail).ToList();
        var history=NamedEtfHoldingHistory.Evaluate(new(key,input.AsOfDate,input.SnapshotCutoff,input.Fund?.Metadata,input.Holdings));
        AlertWindows? window=null;MinimumReductionProof proof=new("InsufficientEvidence",null,null,null,null,[],[],"缺少可核验的具名ETF持仓与同口径份额。");
        if(input.Fund is {} fund)
        {
            if(fund.Key!=key||fund.Points.Any(x=>x.Date>input.AsOfDate)||fund.EvaluatedAt!=input.SnapshotCutoff)throw new InvalidDataException("ETF份额证券、日期或快照身份不符。");
            window=ExitAlertEvaluator.Windows(fund,input.AsOfDate,input.ShareRules);proof=ExitAlertEvaluator.Minimum(history.Baseline.Holdings,fund,input.AsOfDate,input.SnapshotCutoff);
        }
        var shareReady=window?.Complete==true&&input.Acceptance is {} acceptance&&acceptance.Market==key.Market&&acceptance.Ticker==key.Ticker&&acceptance.Through==input.AsOfDate&&acceptance.DataAcceptancePassed;
        components.Add(new("shares","ETF份额与NAV金额",shareReady,input.AsOfDate,window?.State??"Unavailable","份额变化及同日NAV金额属于同一基金整体观察，不代表机构资金流。",window?.RevisionIds??[]));
        var namedReady=proof.State=="DerivedMinimumReduction"&&proof.BaselineDate is {} baseline&&input.AsOfDate.DayNumber-baseline.DayNumber<=rules.MaximumDisclosureAgeDays;
        components.Add(new("named-holdings","具名ETF持仓",namedReady,history.Baseline.ReportAsOf,proof.State,proof.Explanation+" 最新已核披露子集，不代表全部机构账户；排除原因见具名持仓变化页。",proof.EvidenceIds.Append(history.Fingerprint).ToArray()));
        var cycle=input.Cycle;var cycleReady=cycle is{DataStatus:MarketCycleDataStatus.ResearchPreview}&&cycle.AsOfDate==input.AsOfDate&&cycle.EvaluationCutoff==input.SnapshotCutoff&&ContentFingerprint.IsSha256Hex(input.CycleReportFingerprint);
        components.Add(new("cycle","市场周期与成交",cycleReady,cycle?.AsOfDate,cycle?.DataStatus.ToString()??"Unavailable","价格、市场成交和广度沿用独立周期模块；不会再作为三个独立联合分数。",input.CycleReportFingerprint is {} cycleHash?[cycleHash]:[]));
        var nav=input.Nav;var navReady=nav is not null&&nav.Key==key&&nav.KnowledgeCutoff==input.SnapshotCutoff&&ContentFingerprint.IsSha256Hex(nav.Fingerprint)&&new[]{nav.Window5,nav.Window20}.All(x=>x is not null&&x.Date==input.AsOfDate&&x.State=="Complete"&&x.EstimatedAmountYuan.HasValue)&&nav.Window5!.Window==5&&nav.Window20!.Window==20;
        components[0]=components[0] with{Ready=shareReady&&navReady,State=shareReady&&navReady?"CompleteStudyWindows":"IncompleteShareOrNavEvidence"};
        if(!navReady)blocks.Add("ETF份额与NAV金额：同日5/20日NAV金额估计不完整。");
        var direction=input.Direction;var directionReady=direction is{Period:{} period,ActiveSecurities:>0,Gaps:0,UnknownNatures:0}
            &&period<=input.AsOfDate&&input.AsOfDate.DayNumber-period.DayNumber<=rules.MaximumDisclosureAgeDays&&direction.ObservedAt==input.SnapshotCutoff
            &&direction.DisclosedSecurities==direction.ActiveSecurities&&direction.ClassifiedSecurities==direction.DisclosedSecurities&&direction.PricedSecurities==direction.DisclosedSecurities
            &&direction.Groups.Count>0&&direction.Groups.All(x=>x.SampleWeightPercent is >=0 and <=100&&x.Nature is "Strategic" or "Portfolio"&&x.MarketValueCny is >=0)
            &&direction.OriginalHashes.Count>0&&direction.OriginalHashes.All(ContentFingerprint.IsSha256Hex)&&ContentFingerprint.IsSha256Hex(direction.Fingerprint);
        components.Add(new("direct-equity","直接持股样本方向",directionReady,direction?.Period,directionReady?"ReviewedSample":"IncompleteSample","只描述同报告期样本，战略持股与证券投资分开；不等于完整组合或当日减持。",direction?.OriginalHashes??[]));
        var industry=input.Industry;var industryReady=industry is{CalendarVerified:true}&&industry.Through==input.AsOfDate&&industry.Cutoff==input.SnapshotCutoff&&industry.Sectors.Count==IndustryPeResearch.Sectors.Count
            &&industry.Sectors.Select(x=>x.Code).Distinct(StringComparer.Ordinal).Count()==industry.Sectors.Count
            &&industry.Sectors.All(x=>IndustryPeResearch.Sectors.TryGetValue(x.Code,out var name)&&name==x.Name&&x.LatestDate==input.AsOfDate&&IndustryPePosition.Classify(x)!=IndustryPeObservation.EvidenceIncomplete)&&ContentFingerprint.IsSha256Hex(industry.Fingerprint);
        components.Add(new("industry","行业公布PE位置",industryReady,input.AsOfDate,industryReady?"PublishedSeriesResearch":"IncompleteHistory","剔亏样本PE历史位置只作辅助核查；PB、盈利质量和全行业合理价值未评估。",industry is null?[]:[industry.Fingerprint]));
        foreach(var component in components.Where(x=>!x.Ready))blocks.Add(component.Title+"：数据、口径、时效或原件门槛不足。");
        if(cycleReady&&cycle!.TopRiskLevel==MarketCycleSignalLevel.Elevated)support.Add("独立周期模块触发高位风险候选。");
        if(window?.Triggered==true)support.Add("所选ETF达到既有5日或20日份额收缩条件。");
        var matchingFlow=navReady&&window is not null&&((window.Pct5<=-input.ShareRules.Decline5&&nav!.Window5!.EstimatedAmountYuan<0)||(window.Pct20<=-input.ShareRules.Decline20&&nav!.Window20!.EstimatedAmountYuan<0));
        if(matchingFlow)support.Add("同一收缩窗口的NAV金额估计为负；与份额变化同源，不重复计分。");
        if(namedReady&&proof.LowerBound>0)support.Add("相对具名披露基准存在最低净减少下界；不能定位为今日交易。");
        if(cycleReady&&cycle!.BottomCandidateLevel==MarketCycleSignalLevel.Candidate)counter.Add("独立周期模块同时要求关注底部形成条件，不能把份额收缩直接解释为高点。");
        if(cycleReady&&cycle!.RecoveryConfirmation)counter.Add("周期模块出现修复候选条件，需与高位风险分别解释。");
        if(industryReady)foreach(var sector in industry!.Sectors.Where(x=>IndustryPePosition.Classify(x)==IndustryPeObservation.DualHistoricalLow))counter.Add(sector.Name+"的两种公布PE口径处历史低位，不能把市场风险推广为全部行业高估。");
        counter.Add("直接持股方向是滞后披露样本；份额、NAV估计和最低净减少下界不能证明机构当日成交。");
        if(history.Baseline.Exclusions.Count>0)counter.Add($"具名ETF基准选用{history.Baseline.ReportAsOf:yyyy-MM-dd}的已披露子集，{history.Baseline.Exclusions.Count}条旧期、重复或不可用记录未计入；不沿用未列示账户。完整归一记录保留在输入中。");
        var ready=blocks.Count==0;var state=!ready?"Unavailable":cycle!.TopRiskLevel==MarketCycleSignalLevel.Elevated&&window!.Triggered&&matchingFlow&&proof.LowerBound>0?"ReviewCandidate":"NoJointCandidate";
        var canonical=JsonSerializer.SerializeToUtf8Bytes(new{Version=JointExitStudyReport.Version,Input=input,Rules=rules,State=state,DataReady=ready,Components=components,SupportingEvidence=support,CounterEvidence=counter,BlockingReasons=blocks,MinimumReduction=proof});
        return new(input,rules,state,ready,components,support,counter,blocks,proof,ContentFingerprint.Sha256(canonical));
    }
}
