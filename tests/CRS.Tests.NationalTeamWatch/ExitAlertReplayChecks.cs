using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>合成回放检查穿越、缺口、样本分母及可复算性，不冒充真实策略收益验证。</summary>
internal static class ExitAlertReplayChecks
{
    public static void Run()
    {
        void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS 回放："+name);}
        void Reject(AlertReplayRequest value,string name)
        {try{ExitAlertReplay.Run(value);}catch(InvalidDataException){Check(true,name);return;}throw new Exception(name);}
        var days=Enumerable.Range(0,25).Select(i=>new DateOnly(2026,1,1).AddDays(i)).ToArray();
        var cutoff=new DateTimeOffset(2026,1,25,23,59,59,TimeSpan.FromHours(8));
        var members=Enumerable.Range(0,3).Select(i=>new AlertReplayMember(new(new("SSE","51030"+i),"合成ETF",
            days,days.Select((d,n)=>new SharePoint(d,n<20?1000:800,null,$"r{i}-{n}")).ToArray(),
            new("SSE","51030"+i,"fund"+i,"manager"+i,"CSI300",days[0],days[^1],"https://www.sse.com.cn/replay.pdf",new('a',64),"fixture",true),
            true,cutoff),days[0],days[^1])).ToArray();
        var request=new AlertReplayRequest(days[20],days[^1],cutoff,new(),members,[],"预先固定的三个合成成员");
        var report=ExitAlertReplay.Run(request);
        Check(report.FirstTriggers.Count(x=>x.RuleId=="EtfShareContraction")==3&&report.FirstTriggers.All(x=>x.Date==days[20]),"预热窗口保留，首次模拟触发日可复算");
        Check(report.FirstTriggers.Count(x=>x.RuleId=="SampleBreadth")==2&&report.Coverage.All(x=>x.Valid5==5&&x.Valid20==5),"5/20日独立统计，集群复用相同窗口");
        Check(report.TimingProof=="NotProven"&&report.Days.SelectMany(x=>x.Findings).All(x=>x.Preview&&x.Severity!=AlertSeverity.Critical),"回填不证明当时通知，不虚构Critical");
        Check(ExitAlertReplay.Run(request).InputFingerprint==report.InputFingerprint,"冻结输入与规则指纹稳定");
        var missing=members[0] with{Input=members[0].Input with{Points=members[0].Input.Points.Where(x=>x.Date!=days[19]).ToArray()}};
        var gap=ExitAlertReplay.Run(request with{Members=[missing,..members.Skip(1)]});
        Check(!gap.Days[0].Findings.Any(x=>x.RuleId=="SampleBreadth")&&gap.Coverage[0].Valid5==0,"预热期缺口阻断窗口和集群，不跨日补齐");
        var calendarMissing=members[0] with{Input=members[0].Input with{TradingDays=[]}};
        Check(!ExitAlertReplay.Run(request with{Members=[calendarMissing,..members.Skip(1)]}).FirstTriggers.Any(x=>x.RuleId=="SampleBreadth"),"无日历成员仍留在集群分母");
        var holding=new NamedEtfHolding("h","SSE","510300","legal1","具名法人","account",days[0],
            new(2026,1,22,9,0,0,TimeSpan.FromHours(8)),new(2026,1,23,9,0,0,TimeSpan.FromHours(8)),900,
            "https://www.sse.com.cn/holding.pdf",new('b',64),"第1页","fixture",true,true);
        var named=ExitAlertReplay.Run(request with{Holdings=[holding]});
        Check(named.FirstTriggers.Single(x=>x.RuleId=="MinimumReduction").Date==new DateOnly(2026,1,23),"1月22公告且23日首见，不能用于21日或22日证明");
        var dateOnly=holding with{PublishedAt=null,PublicationDate=new(2026,1,22),PublicationPrecision=EtfPublicationPrecision.DateOnly};
        Check(ExitAlertReplay.Run(request with{Holdings=[dateOnly]}).FirstTriggers.Single(x=>x.RuleId=="MinimumReduction").Date==new DateOnly(2026,1,23),"公告仅日期的回放仍由真实首见限制，不提前至公告日");
        Check(!ExitAlertReplay.Run(request with{Holdings=[holding with{LegalName="机构1"}]}).FirstTriggers.Any(x=>x.RuleId=="MinimumReduction"),"匿名数额匹配不形成具名回放证明");
        Check(ExitAlertReplay.Run(request with{Rules=request.Rules with{Enabled=false}}).FirstTriggers.Count==0,"禁用规则保留覆盖统计但不触发");
        Reject(request with{Members=[members[0],members[0]]},"重复样本拒绝");
        Reject(request with{KnowledgeCutoff=cutoff.AddDays(-1)},"全局截止时间之外的回放拒绝");
        Reject(request with{Members=[members[0] with{Input=members[0].Input with{Points=[..members[0].Input.Points,members[0].Input.Points[0]]}}]},"同日冲突修订必须先审核");
        var directory=Path.Combine(Environment.CurrentDirectory,"artifacts","exit-alert-replay-synthetic");Directory.CreateDirectory(directory);
        var source=Path.Combine(directory,"fixture.json");File.WriteAllText(source,System.Text.Json.JsonSerializer.Serialize(request));
        ExitAlertReplayTool.FromFile(source,directory).GetAwaiter().GetResult();
        var exported=System.Text.Json.JsonSerializer.Deserialize<AlertReplayReport>(File.ReadAllText(Path.Combine(directory,"report.json")))!;
        var restored=System.Text.Json.JsonSerializer.Deserialize<AlertReplayRequest>(File.ReadAllText(Path.Combine(directory,"inputs.json")))!;
        Check(exported.InputFingerprint==ExitAlertReplay.Run(restored).InputFingerprint&&File.ReadAllText(Path.Combine(directory,"report.md")).Contains("NotProven"),"文件导出及重新载入得到相同指纹，中文报告标明时效未证明");
        var old=report.FrozenInputs.Members[0].Input.Name;members[0]=members[0] with{Input=members[0].Input with{Name="后来修改"}};
        Check(report.FrozenInputs.Members[0].Input.Name==old,"调用后修改样本不改变报告快照");
    }
}
