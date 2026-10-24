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
Check(a.OpportunityState=="ResearchWatch"&&report.TimingProof=="NotProven","完整证据只产生研究观察条件，不生成收益概率或买入指令");
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
