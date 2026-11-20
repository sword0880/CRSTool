using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>联合观察协调器只读同一快照，金融模型不放回聚合仓储或通知协调器。</summary>
internal sealed class JointExitStudyStore(IMarketRepositorySession session,EtfStore etf,AlertStore alerts,EtfEvidenceStore evidence,
    EtfNavStudyStore nav,MarketCycleStudyStore cycle,IndustryPeStore industry,MarketCalendarStore calendar,EquityStore equity)
{
    private sealed record FundPack(AlertFundInput Input,IReadOnlyList<NamedEtfHolding> Holdings,EtfAcceptance? Acceptance);
    public JointExitStudyReport Read(EtfKey key,DateOnly asOf)
    {
        if(!session.IsReadSnapshot||session.ReadSnapshotCutoff is not {} captured)throw new InvalidOperationException("联合观察必须使用独立一致性读快照。");
        var cutoff=CRS.Timing.BeijingClock.Normalize(captured);
        if(asOf.DayNumber<400||asOf>=DateOnly.FromDateTime(cutoff.DateTime))throw new InvalidDataException("联合观察只接受已结束的统计日。");
        var configuration=etf.Configuration().SingleOrDefault(x=>x.Market==key.Market&&x.Ticker==key.Ticker&&x.Active)??throw new InvalidDataException("请选择当前配置池中启用的ETF。");
        var issues=new List<JointExitIssue>();var rules=alerts.AlertConfiguration().Rules;
        T? ReadPart<T>(string component,Func<T> read) where T:class
        {
            session.OperationCancellation.ThrowIfCancellationRequested();
            try{return read();}
            catch(Exception error) when(error is IOException or InvalidDataException or FormatException or JsonException or NotSupportedException)
            {issues.Add(new(component,"ComponentUnavailable","该分量查询或原件核验不可用，未以空值或零替代成功。"));return null;}
        }
        var fund=ReadPart("ETF份额与具名持仓",()=>
        {
            var acceptance=etf.Acceptance(key,asOf).SingleOrDefault();var data=(IAlertMarketFacts)etf;
            var days=data.Calendar(key.Market).Where(x=>x.Date<=asOf).TakeLast(251).ToArray();var points=etf.Points(key).Where(x=>x.Date<=asOf&&days.Any(d=>d.Date==x.Date)).ToArray();
            var metadata=evidence.MetadataForNavStudy(key);
            var input=new AlertFundInput(key,configuration.Name,days.Select(x=>x.Date).ToArray(),points,metadata,acceptance?.CalendarVerified==true&&days.All(x=>x.Valid),cutoff);
            return new FundPack(input,evidence.NamedEtfHoldings(key).ToArray(),acceptance);
        });
        var market=ReadPart("市场周期与成交",()=>cycle.Read(asOf,cutoff));
        var navReport=ReadPart("NAV金额",()=>EtfNavStudy.Run(nav.Freeze(key,asOf,asOf)));
        var direct=ReadPart("直接持股样本方向",()=>equity.EquityDirection(null));
        var pe=ReadPart("行业公布PE",()=>
        {
            var from=asOf.AddDays(-400);var cal=calendar.IndustryPeCalendar(from,asOf,cutoff);
            return IndustryPeResearch.Calculate(from,asOf,cutoff,industry.IndustryPeRevisions(from,asOf,cutoff),cal.Days,cal.Fingerprint);
        });
        JointExitNavContext? navContext=navReport is null?null:new(navReport.FrozenInputs.Key,navReport.FrozenInputs.KnowledgeCutoff,navReport.Windows.SingleOrDefault(x=>x.Window==5&&x.Date==asOf),navReport.Windows.SingleOrDefault(x=>x.Window==20&&x.Date==asOf),navReport.Fingerprint);
        JointExitDirectionContext? direction=direct is null?null:new(direct.ReportAsOf,direct.ObservedAt,direct.ActiveSecurities,direct.DisclosedSecurities,direct.ClassifiedSecurities,direct.PricedSecurities,direct.Gaps.Count,
            direct.Positions.Count(x=>x.Holding.Nature is not ("Strategic" or "Portfolio")),direct.Groups,direct.Positions.Select(x=>x.Holding.EvidenceHash).Where(x=>x is not null).Select(x=>x!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(direct)));
        // 联合规则只消费各分量的归一输出；完整价格/原件/曲线可从对应工作区按所列指纹核查，不伪称本报告含全部原件。
        JointExitIndustryContext? valuation=pe is null?null:new(pe.Through,pe.Cutoff,pe.CalendarVerified,pe.Rows.Select(x=>x with{Curve=[]}).ToArray(),pe.Fingerprint);
        var input=new JointExitInput(configuration,asOf,cutoff,rules,fund?.Input,fund?.Holdings??[],fund?.Acceptance,market?.Assessment,market?.Fingerprint,navContext,direction,valuation,issues);
        return Domain.JointExitStudy.Evaluate(input);
    }
}
