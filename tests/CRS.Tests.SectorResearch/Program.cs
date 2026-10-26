using CRS.Plugins.NationalTeamWatch.Domain;
using System.Text.Json;

static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS 行业研究："+name);}
static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new Exception("未拒绝："+name);}
var cutoff=new DateTimeOffset(2026,10,24,12,0,0,TimeSpan.Zero);var through=new DateOnly(2026,10,23);var from=new DateOnly(2026,10,1);
SectorEvidence Proof(string id,char hash='a')=>new(id,new(hash,64),cutoff,cutoff,cutoff,"Accepted");
// 合成日历与估值只用于算法反例；不代表交易所日历或真实行业机会。
var days=Enumerable.Range(0,388).Select(i=>new DateOnly(2025,10,1).AddDays(i)).ToArray();
SectorInput Input(string id,decimal scale)=>new(new(id,"合成行业"+id,"synthetic","v1","PriceIndex","CNY","aggregate-v1","universe-v1",new(2020,1,1),new(2030,1,1)),
    days.Select((d,i)=>new SectorPrice(d,(100+i)*scale,Proof(id+"p"+i))).ToArray(),
    days.Select((d,i)=>new SectorValuation(d,d==through?5:20,d==through?0.5m:2,true,true,1,"aggregate-v1","universe-v1",new('b',64),Proof(id+"v"+i,'b'))).ToArray(),
    [new(new(2026,9,30),0.1m,1,Proof(id+"f",'c'))]);
var input=new SectorStudyRequest(from,through,cutoff,new(2025,10,1),through,Proof("calendar",'d'),days,[Input("A",1),Input("B",10)],HistoryYears:1);
var report=SectorResearch.Calculate(input);var a=report.Rows[0];
Check(report.BaseDate==from&&a.Curve[0].NormalizedClose==100&&a.Curve.SequenceEqual(report.Rows[1].Curve),"不同指数点位统一同一日期起点100，不比较绝对点位");
Check(a.PeSamples==365&&a.PbSamples==365&&a.ExpectedHistory==365&&a.PePercentile==0&&a.PbPercentile==0,"历史分位只用当日之前同口径记录，样本和分母明确");
Check(a.OpportunityState=="IndependentQualityEvidenceUnavailable"&&a.QualityReadiness=="MissingQualitySource"
    &&report.TimingProof=="NotProven","只有利润增长与低PE/PB时不再输出ResearchWatch，独立质量证据保持未知");
SectorStudyReport One(SectorInput x)=>SectorResearch.Calculate(input with{Sectors=[x]});
var original=input.Sectors[0];
Check(One(original with{Prices=original.Prices.Where(x=>x.Date!=from).ToArray()}).Rows[0] is {PriceState:"MissingCommonBase"} missing&&missing.Curve.All(x=>x.NormalizedClose is null),"缺共同起点不偷换成自己的首日");
var gap=One(original with{Prices=original.Prices.Where(x=>x.Date!=from.AddDays(5)).ToArray()}).Rows[0];
Check(gap.PriceState=="Gaps"&&gap.Curve[5].NormalizedClose is null&&gap.Curve[4].NormalizedClose.HasValue&&gap.Curve[6].NormalizedClose.HasValue,"缺日保持空值，既不补零也不前值填充");
var normal=original.Valuations.Select(x=>x.Date==through?x with{PeTtm=20,Pb=2}:x).ToArray();
Check(One(original with{Valuations=normal}).Rows[0] is {PePercentile:50,PbPercentile:50,OpportunityState:"NoLowValuationCondition"},"同值使用中间秩，估值没有变低时不因价格下跌而称便宜");
Check(One(original with{Valuations=original.Valuations.Where(x=>x.Date!=through).ToArray()}).Rows[0].ValuationState=="MissingCurrentValuation","缺当期估值不拿旧值冒充最新");
Check(One(original with{Valuations=original.Valuations.Select(x=>x.Date==through?x with{PeTtm=-1}:x).ToArray()}).Rows[0] is {PeTtm:null,OpportunityState:"NotEvaluated"},"负PE不是低估，不强行排名");
Check(One(original with{Valuations=original.Valuations.Select(x=>x.Date==through?x with{Coverage=0.2m}:x).ToArray()}).Rows[0].ValuationState=="UnverifiedValuation","低市值覆盖不当作全行业估值");
Check(One(original with{Valuations=original.Valuations.TakeLast(100).ToArray()}).Rows[0].ValuationState=="InsufficientHistory","历史不足不输出虚假分位");
Check(One(original with{Fundamentals=[]}).Rows[0].OpportunityState=="FundamentalsUnavailable","历史便宜但没有基本面时保留无法判断");
Check(One(original with{Fundamentals=[original.Fundamentals[0] with{ProfitGrowth=-0.2m}]}).Rows[0].OpportunityState=="ValueTrapRisk","盈利恶化单独提示风险，不升级观察机会");
Check(One(original with{Fundamentals=[original.Fundamentals[0] with{AsOf=new(2025,1,1)}]}).Rows[0].OpportunityState=="FundamentalsUnavailable","过期基本面不沿用");
Check(One(original with{Valuations=original.Valuations.Select(x=>x.Date==through?x with{Method="other"}:x).ToArray()}).Rows[0].ValuationState=="UnverifiedValuation","估值计算口径变化不混接");
Check(One(original with{Definition=original.Definition with{ValidFrom=from}}).Rows[0].ValuationState=="InsufficientHistory","行业映射只从生效日开始，不能把新分类回填旧历史");
Check(One(original with{Valuations=original.Valuations.Select(x=>x.Date==through?x with{Evidence=x.Evidence with{AcceptedAt=cutoff.AddTicks(1)}}:x).ToArray()}).Rows[0].OpportunityState=="NotEvaluated","知识截止后的审核不可用于当前判断");
Reject(()=>SectorResearch.Calculate(input with{Sectors=[original,original]}),"重复行业身份拒绝");
Reject(()=>SectorResearch.Calculate(input with{Sectors=[original,input.Sectors[1] with{Definition=input.Sectors[1].Definition with{Currency="USD"}}]}),"不同币种不能直接叠图");
Reject(()=>SectorResearch.Calculate(input with{Sectors=[original,input.Sectors[1] with{Definition=input.Sectors[1].Definition with{Scheme="other"}}]}),"行业分类不能混用");
Reject(()=>One(original with{Prices=[..original.Prices,original.Prices[0]]}),"同日冲突必须先审核");
Check(SectorResearch.Calculate(input with{Sectors=input.Sectors.Reverse().ToArray()}).Fingerprint==report.Fingerprint,"输入顺序不改变研究指纹");
Check(One(original with{Prices=original.Prices.Select(x=>x with{Evidence=x.Evidence with{Hash=new('e',64)}}).ToArray()}).Fingerprint!=One(original).Fingerprint,"来源换版即使数值相同也改变指纹");
var changing=new AlternatingPrices(original.Prices,original.Prices.Select((p,i)=>p with{Close=p.Close*(1m+i/1000m)}).ToArray());
var frozen=One(original with{Prices=changing});var stable=One(original);
Check(frozen.Fingerprint==stable.Fingerprint&&frozen.Rows[0].Curve.SequenceEqual(stable.Rows[0].Curve),"调用方可变集合不能让图表和指纹绑定到不同版本");
// 独立基本面质量闸门：显式合成财务资料；只能做“资料齐备、进入人工反方复核”的判断。
SectorEvidence QualityProof(string id)=>Proof(id,'e') with{PublishedAt=cutoff.AddDays(-2)};
IndustryQualityObservation Fact(IndustryQualityMeasure metric,decimal value)=>new(metric,value,
    metric==IndustryQualityMeasure.BalanceSheetLeverage?IndustryQualityUnit.Multiple:IndustryQualityUnit.Ratio,
    new(2026,9,30),0.96m,"synthetic-reviewed-v1","universe-v1",new('b',64),
    "source-"+metric,true,false,QualityProof("proof-"+metric));
var profile=new IndustryQualityInput("A","synthetic","v1",IndustryQualityProfile.Cyclical,
    [Fact(IndustryQualityMeasure.MidCycleEarnings,0.08m),Fact(IndustryQualityMeasure.CashConversion,1.1m),
        Fact(IndustryQualityMeasure.BalanceSheetLeverage,2.1m)])
    {ProfileRuleSet="industry-profile-v1",ProfileMappingEvidence=QualityProof("synthetic-profile-cyclical")};
IndustryQualityGateReport Gate(IndustryQualityInput candidate,string? hash=null)=>IndustryQualityGate.Evaluate(original.Definition,candidate,through,cutoff,hash??new('b',64));
var ready=Gate(profile);
Check(ready.State=="ReadyForAdversarialReview"&&ready.InvestmentConclusion=="NotEvaluated"
    &&ready.Metrics.Count==3,"周期行业全部人工核验资料齐备仍然不能推导已低估");
var enriched=SectorResearch.Calculate(input with{QualityInputs=[profile]});
Check(enriched.Rows[0].OpportunityState=="NeedsAdversarialAssessment"&&enriched.Rows[0].QualityReadiness=="ReadyForAdversarialReview"
    &&enriched.TimingProof=="NotProven"&&enriched.Rows[1].OpportunityState=="IndependentQualityEvidenceUnavailable",
    "只有关联行业自己的完整质量证据才可进入研究观察，且另一行业仍未知");
Check(enriched.Fingerprint!=report.Fingerprint,"独立质量证据进入研究输入指纹，不可与此前版本混同");
Check(SectorResearch.Calculate(input with{QualityInputs=[profile with{Observations=profile.Observations.Reverse().ToArray()}]}).Fingerprint==enriched.Fingerprint,
    "独立指标输入顺序不应改变研究身份");
Check(Gate(profile with{Observations=profile.Observations.Take(2).ToArray()}).State=="IncompleteQualityEvidence",
    "缺杠杆信息不能声称周期行业证据齐全");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{ResearchUseAllowed=false}:x).ToArray()}).State=="IncompleteQualityEvidence","没有来源研究准入授权不能升级状态");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{DerivedFromValuation=true}:x).ToArray()}).State=="IncompleteQualityEvidence","PE推导的伪基本面不能成为独立证据");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{UniverseSnapshotHash=new('f',64)}:x).ToArray()}).State=="IncompleteQualityEvidence","行业公司池快照不一致不允许拼接");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{MarketCapCoverage=0.79m}:x).ToArray()}).State=="IncompleteQualityEvidence","市值覆盖低于80%视为缺失");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{PeriodEnd=new(2025,1,1)}:x).ToArray()}).State=="IncompleteQualityEvidence","财报过旧不能覆盖当前行业估值");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{Unit=IndustryQualityUnit.Months}:x).ToArray()}).State=="IncompleteQualityEvidence","倍率/百分比/月数混用必须拒绝");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{Evidence=x.Evidence with{PublishedAt=null}}:x).ToArray()}).State=="IncompleteQualityEvidence","没有财报披露时点不得推断可交易历史");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{Evidence=x.Evidence with{AcceptedAt=cutoff.AddSeconds(1)}}:x).ToArray()}).State=="IncompleteQualityEvidence","知识截止后的审批不能穿越时点");
Check(Gate(profile with{Scheme="other"}).State=="IncompatibleUniverseOrClassification","行业分类未匹配不能沿用质量证据");
Check(Gate(profile,"bad-hash").State=="IncompatibleUniverseOrClassification","未知公司池摘要不能升级质量状态");
Check(Gate(profile with{ProfileMappingEvidence=null}).State=="UnverifiedProfileMapping",
    "行业映射未经过审核不能任意自选更宽松的规则");
Check(Gate(profile with{ProfileRuleSet="old-method"}).State=="UnverifiedProfileMapping",
    "来源分类方法换版后需重新核验，不能沿用旧规则");
Check(Gate(profile with{ProfileMappingEvidence=QualityProof("future-mapping") with{AcceptedAt=cutoff.AddSeconds(1)}}).State=="UnverifiedProfileMapping",
    "知识截止之后才审核的行业规则不能提前生效");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{Evidence=x.Evidence with{PublishedAt=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero)}}:x).ToArray()})
    .State=="IncompleteQualityEvidence","财报披露早于报告期结束不可能是真实证据");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x.Measure==IndustryQualityMeasure.CashConversion?
    x with{Value=400m}:x).ToArray()}).State=="IncompleteQualityEvidence",
    "离谱百分比必须复核单位，不得自动列为证据完备");
Check(Gate(profile with{Profile=IndustryQualityProfile.Financial}).State=="IncompleteQualityEvidence",
    "金融行业必须包含资产质量和资本适足率，不能套周期行业现金流检查");
Check(Gate(profile with{Profile=IndustryQualityProfile.Innovation}).State=="IncompleteQualityEvidence",
    "创新型医药/科技须有研发资金覆盖与现金续航指标");
Reject(()=>Gate(profile with{Observations=[..profile.Observations,profile.Observations[0]]}),"重复的基本面指标需要人工审核，不能择优");
Reject(()=>SectorResearch.Calculate(input with{QualityInputs=[profile,profile]}),"同一行业不能附带两份冲突质量资料");
Reject(()=>SectorResearch.Calculate(input with{QualityInputs=[profile with{SectorId="unknown"}]}),"未知行业质量资料不能偷偷并入");
Check(SectorResearch.Calculate(input with{QualityInputs=[profile with{Observations=profile.Observations.Take(2).ToArray()}]})
        .Rows[0].OpportunityState=="IndependentQualityEvidenceUnavailable","缺一项独立质量指标时必须降级");
Check(Gate(profile with{Observations=profile.Observations.Select(x=>x with{Evidence=x.Evidence with{Hash=new('a',64)}}).ToArray()}).Fingerprint
    !=ready.Fingerprint,"即使质量数值相同，改换来源原件也改变质量报告指纹");
// 仅显式指定目录时导出合成检查报告，便于与真实验收分开存放。
if(args is ["--output",var output]){Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"synthetic-input.json"),JsonSerializer.Serialize(input));File.WriteAllText(Path.Combine(output,"synthetic-report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));}
// 用交替读取模拟外部集合在计算期间被修改，避免只验证计算后的普通赋值。
sealed class AlternatingPrices(IReadOnlyList<SectorPrice> first,IReadOnlyList<SectorPrice> second):IReadOnlyList<SectorPrice>
{
    private int reads;
    public int Count=>first.Count;
    public SectorPrice this[int index]=>first[index];
    public IEnumerator<SectorPrice> GetEnumerator()=>(++reads%2==1?first:second).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
}
