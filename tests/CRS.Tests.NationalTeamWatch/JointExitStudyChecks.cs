using System.Text.Json;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Presentation;
using CRS.Plugin.Abstractions;
using CRS.Serialization;

internal static class JointExitStudyChecks
{
    private static readonly DateOnly Day=new(2026,10,20);
    private static readonly DateTimeOffset Cutoff=new(2026,10,28,8,0,0,TimeSpan.FromHours(8));
    private static readonly string Hash=new('A',64);
    private const string Url="https://www.sse.com.cn/synthetic-joint-study.pdf";
    private static int passed;
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);var input=Fixture();var candidate=JointExitStudy.Evaluate(input);
        Check(candidate is{State:"ReviewCandidate",DataReady:true,NotificationsEnabled:false,TimingProof:"NotProven",ModelValidation:"NotValidated"}&&candidate.Components.Count==5&&candidate.MinimumReduction.LowerBound==90,"合成归一输入能形成核查候选；仍未验证模型且禁止通知");
        Check(JointExitStudy.Evaluate(input).Fingerprint==candidate.Fingerprint,"相同归一输入与规则确定复算");
        var older=input.Holdings[0] with{Id="older-period",HoldingAsOf=input.Holdings[0].HoldingAsOf.AddDays(-20)};
        var multiple=JointExitStudy.Evaluate(input with{Holdings=[older,..input.Holdings]});
        Check(multiple.State=="ReviewCandidate"&&multiple.CounterEvidence.Any(x=>x.Contains("未计入"))&&multiple.Input.Holdings.Count==2,"多期披露明确选最新子集并保留排除说明，不把旧期混入下界");
        Check(!JointExitStudy.Evaluate(input with{Holdings=[..input.Holdings,input.Holdings[0] with{Id="conflict-latest",Shares=951}]}).DataReady,"联合观察最新披露冲突时关闭，不退回旧基准");
        var privateHolder=input.Holdings[0] with{Id="private-holder",LegalEntityId="private-id",LegalName="合成私募法人",Account="private-account",Shares=100000};
        var withPrivate=JointExitStudy.Evaluate(input with{Holdings=[input.Holdings[0] with{Shares=800},privateHolder]});
        Check(withPrivate.State=="NoJointCandidate"&&withPrivate.MinimumReduction.LowerBound==0,"目录外具名持有者不能制造国家队最低净减少下界");
        Check(!JointExitStudy.Evaluate(input with{Holdings=[privateHolder]}).DataReady,"仅有目录外主体披露时关闭联合判断");
        var roundTrip=JsonSerializer.Deserialize<JointExitStudyReport>(JsonSerializer.Serialize(candidate))!;
        Check(JointExitStudy.Evaluate(roundTrip.Input,roundTrip.Rules).Fingerprint==candidate.Fingerprint,"JSON分块传输前后的输入复算一致");
        var missing=new[]{input with{Fund=null},input with{Nav=null},input with{Holdings=[]},input with{Cycle=null},input with{Direction=null},input with{Industry=null}};
        foreach(var value in missing)Check(JointExitStudy.Evaluate(value) is{State:"Unavailable",DataReady:false},"每个关键分量缺失均关闭联合判断："+Array.IndexOf(missing,value));
        Check(!JointExitStudy.Evaluate(input with{Acceptance=input.Acceptance! with{ContinuousCollectionDays=19}}).DataReady,"合成250日覆盖不能代替连续20日采集门槛");
        Check(JointExitStudy.Evaluate(input with{Cycle=input.Cycle! with{TopRiskLevel=MarketCycleSignalLevel.None}}) is{State:"NoJointCandidate",DataReady:true},"资料齐全但没有高位条件，不形成联合候选");
        Check(JointExitStudy.Evaluate(input with{Nav=input.Nav! with{Window5=input.Nav.Window5! with{EstimatedAmountYuan=1},Window20=input.Nav.Window20! with{EstimatedAmountYuan=1}}}).State=="NoJointCandidate","NAV估计不是负金额时不能仅凭份额收缩形成联合候选");
        var onlyFive=input with{Fund=input.Fund! with{Points=input.Fund.Points.Select(p=>p.Date==Day.AddDays(-20)?p with{Shares=860}:p).ToArray()},Nav=input.Nav! with{Window5=input.Nav.Window5! with{EstimatedAmountYuan=1},Window20=input.Nav.Window20! with{EstimatedAmountYuan=-1}}};
        Check(JointExitStudy.Evaluate(onlyFive).State=="NoJointCandidate","5日份额收缩不能搭配另一个20日负金额窗口");
        Check(JointExitStudy.Evaluate(input with{Holdings=[input.Holdings[0] with{Shares=800}]}).State=="NoJointCandidate","具名持仓数学下界为零，不能猜测已减持");
        Check(!JointExitStudy.Evaluate(input with{Holdings=[input.Holdings[0] with{FirstSeenAt=Cutoff.AddSeconds(1)}]}).DataReady,"快照之后才首次取得的具名披露拒绝");
        Check(!JointExitStudy.Evaluate(input with{Direction=input.Direction! with{Period=Day.AddDays(1)}}).DataReady,"未来报告期直接持股不能拼接到统计日");
        Check(!JointExitStudy.Evaluate(input with{Direction=input.Direction! with{Period=Day.AddDays(-371)}}).DataReady,"过期直接持股样本保持资料不足");
        Check(!JointExitStudy.Evaluate(input with{Holdings=[input.Holdings[0] with{HoldingAsOf=Day.AddDays(-371)}],Fund=input.Fund! with{Metadata=input.Fund.Metadata! with{ComparableFrom=Day.AddDays(-400)}}}).DataReady,"具名基准即使可比较，超过研究时效仍关闭判断");
        Check(!JointExitStudy.Evaluate(input with{Direction=input.Direction! with{OriginalHashes=[]}}).DataReady,"没有持股原件引用不通过方向门槛");
        Check(!JointExitStudy.Evaluate(input with{Direction=input.Direction! with{Groups=[input.Direction.Groups[0] with{Nature="Unknown"}]}}).DataReady,"未识别持股性质不能作为完整方向样本");
        Check(!JointExitStudy.Evaluate(input with{Industry=input.Industry! with{Sectors=input.Industry.Sectors.Select((s,i)=>i==0?s with{Name="错误行业"}:s).ToArray()}}).DataReady,"行业代码和名称混配关闭判断");
        Check(!JointExitStudy.Evaluate(input with{Industry=input.Industry! with{Sectors=input.Industry.Sectors.Select((s,i)=>i==0?s with{PriorSamples=251}:s).ToArray()}}).DataReady,"行业少于252日不能包装成完整历史位置");
        Check(!JointExitStudy.Evaluate(input with{Nav=input.Nav! with{Window5=input.Nav.Window5! with{Date=Day.AddDays(-1)}}}).DataReady,"NAV金额日期错位关闭判断");
        Check(!JointExitStudy.Evaluate(input with{Nav=input.Nav! with{Key=new("SZSE","159915")}}).DataReady,"其他ETF的NAV金额不能借给当前证券");
        Check(!JointExitStudy.Evaluate(input with{Nav=input.Nav! with{KnowledgeCutoff=Cutoff.AddSeconds(1)}}).DataReady,"NAV知识截止错位关闭判断");
        Check(!JointExitStudy.Evaluate(input with{Direction=input.Direction! with{ObservedAt=Cutoff.AddSeconds(1)}}).DataReady,"直接持股读取时刻错位关闭判断");
        Check(!JointExitStudy.Evaluate(input with{Industry=input.Industry! with{Cutoff=Cutoff.AddSeconds(1)}}).DataReady,"行业研究知识截止错位关闭判断");
        Check(!JointExitStudy.Evaluate(input with{Cycle=input.Cycle! with{EvaluationCutoff=Cutoff.AddSeconds(1)}}).DataReady,"周期计算截止与同一快照不一致关闭判断");
        var low=input with{Industry=input.Industry! with{Sectors=input.Industry.Sectors.Select(s=>s with{PublishedSeriesPercentile=10,PublishedMedianSeriesPercentile=10}).ToArray()}};
        Check(JointExitStudy.Evaluate(low).CounterEvidence.Count==12,"行业历史低位逐项列为反向核查，不抵消或重复计分");
        Reject(()=>JointExitStudy.Evaluate(input with{Fund=input.Fund! with{Key=new("SZSE","159915")}}),"ETF证券身份错配拒绝");
        Reject(()=>JointExitStudy.Evaluate(input with{AsOfDate=DateOnly.FromDateTime(Cutoff.DateTime)}),"未结束统计日拒绝");
        Reject(()=>JointExitStudy.Evaluate(input with{Configuration=input.Configuration with{Active=false}}),"停用ETF不能伪装成当前配置池");
        var missingReport=JointExitStudy.Evaluate(input with{Fund=null,Holdings=[],Acceptance=null,Cycle=null,Nav=null,Direction=null,Industry=null});
        File.WriteAllText(Path.Combine(root,"candidate.json"),JsonSerializer.Serialize(candidate));File.WriteAllText(Path.Combine(root,"missing.json"),JsonSerializer.Serialize(missingReport));
        await Model(candidate);await Storage(Path.Combine(root,"storage"));await Background(Path.Combine(root,"background"));
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticNormalizedJointInputsAndEmptyIsolatedStore",FinancialModelValidation="NotValidated"},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static JointExitInput Fixture()
    {
        // 仅构造规则归一输入以覆盖候选分支；不伪称原始行情、250/20真实采集或上游验收通过。
        var days=Enumerable.Range(0,21).Select(i=>Day.AddDays(i-20)).ToArray();var config=new EtfConfiguration("SSE","510300","合成ETF","synthetic",true,"fixture","fixture",1);
        var metadata=new AlertFundMetadata("SSE","510300","synthetic-fund","synthetic-manager","synthetic-index",days[0],Day,Url,Hash,"synthetic-reviewer",true);
        var fund=new AlertFundInput(new("SSE","510300"),"合成ETF",days,days.Select((d,i)=>new SharePoint(d,i==20?860:1000,null,"synthetic-share-"+i)).ToArray(),metadata,true,Cutoff);
        var holding=new NamedEtfHolding("synthetic-holding","SSE","510300","synthetic-legal","中央汇金投资有限责任公司","synthetic-account",days[0],Cutoff.AddDays(-10),Cutoff.AddDays(-9),950,Url,Hash,"合成第10页","synthetic-reviewer",true,true);
        var cycle=MarketCycleEvaluator.Evaluate(new("SSE+SZSE","399317",Day,Cutoff,false,[],[],[],[])) with{DataStatus=MarketCycleDataStatus.ResearchPreview,TopRiskLevel=MarketCycleSignalLevel.Elevated,BottomCandidateLevel=MarketCycleSignalLevel.None};
        var nav=new JointExitNavContext(fund.Key,Cutoff,new(Day,5,-140,-280,"Complete",Hash),new(Day,20,-140,-280,"Complete",Hash),Hash);
        var direction=new JointExitDirectionContext(Day.AddDays(-20),Cutoff,1,1,1,1,0,0,[new("合成具名法人","Portfolio","synthetic-scheme","1","C07","金融","未分类",1,1,1000,100)],[Hash],Hash);
        var sectors=IndustryPeResearch.Sectors.Select(s=>new IndustryPeSeries(s.Key,s.Value,[],10,15,50,252,252,"PublishedSeriesResearch",Day){PublishedMedianSeriesPercentile=50,MedianPriorSamples=252}).ToArray();
        return new(config,Day,Cutoff,new(),fund,[holding],new("SSE","510300",Day,Day.AddDays(-365),250,250,0,20,true),cycle,Hash,nav,direction,new(Day,Cutoff,true,sectors,Hash),[]);
    }
    private static async Task Model(JointExitStudyReport report)
    {
        var workspace=new Workspace(report);var model=new JointExitStudyViewModel(workspace,workspace,default);await model.EnsureInitializedAsync();
        Check(model.Funds.Count==1&&model.Report is null&&workspace.Queries==0,"懒加载仅读取当前启用ETF目录，不自动查询或写入");
        model.AsOf=Day.ToDateTime(TimeOnly.MinValue);await model.ReadAsync();Check(model.Components.Count==5&&model.Status.Contains("通知关闭"),"页面展示五项归一结果及关闭通知状态");
        model.AsOf=model.AsOf.Value.AddDays(-1);Check(model.Report is null&&model.Components.Count==0,"统计日改变立即清空旧结果");model.AsOf=Day.ToDateTime(TimeOnly.MinValue);
        workspace.Delayed=true;var first=model.ReadAsync();var second=model.ReadAsync();workspace.Replies[1].SetResult(report);await second;workspace.Replies[0].SetResult(report with{State="Unavailable"});await first;
        Check(model.Report?.State=="ReviewCandidate"&&!model.Busy,"晚到旧响应不能覆盖新结果或忙碌状态");workspace.Delayed=false;workspace.Fail=true;await model.ReadAsync();Check(model.Report is null&&model.Status.Contains("不可用"),"失败不保留旧联合候选");
        workspace.Fail=false;workspace.Report=report with{Input=report.Input with{AsOfDate=Day.AddDays(-1)}};await model.ReadAsync();Check(model.Report is null,"结果日期错位界面拒绝展示");
        workspace.Report=report;workspace.Delayed=true;var canceled=model.ReadAsync();model.CancelCommand.Execute(null);workspace.Replies[^1].SetResult(report);await canceled;Check(model.Report is null&&model.Status.Contains("取消"),"取消后即使上游忽略令牌，迟到结果仍不展示");
    }
    private static async Task Storage(string root)
    {
        var clock=new Clock(Cutoff);var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock);var key=new EtfKey("SSE","510300");
        var counts=repo.AlertEvents(new()).Count;var outbox=repo.AlertOutbox().Count;var snapshot=repo.Read(reader=>{clock.Now=Cutoff.AddHours(1);return reader.JointExitStudy(key,Day);},default);
        Check(snapshot.Input.SnapshotCutoff==Cutoff&&snapshot.Components.Count==5&&!snapshot.DataReady,"同一数据库快照缺少五类数据时保持不可判断，截止不漂移");
        Check(snapshot.Input.Nav?.KnowledgeCutoff==Cutoff&&snapshot.Input.Direction?.ObservedAt==Cutoff&&snapshot.Input.Industry?.Cutoff==Cutoff,"NAV、直接持股和行业输出采用同一快照前时刻");
        Check(repo.AlertEvents(new()).Count==counts&&repo.AlertOutbox().Count==outbox,"联合只读查询不生成金融预警事件或通知条目");
        Reject(()=>repo.Read(r=>r.JointExitStudy(new("SSE","000000"),Day),default),"未配置ETF不能绕过当前观察池");
        await Task.CompletedTask;
    }
    private static async Task Background(string root)
    {
        await using var session=new NationalTeamBackgroundSession(new Context(root),new NoNetwork());await session.StartAsync(default);
        var request=new EtfRequest("joint-exit-study","SSE","510300",To:Day,Count:1);var blocks=new List<JsonDocumentBlock>();
        while(true){var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(request),default));blocks.AddRange(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(page.Items)!);if(page.NextOffset is not {} next)break;request=request with{Offset=next,Snapshot=page.Snapshot};}
        var report=JsonDocumentBlocks.Decode<JointExitStudyReport>(blocks,default);Check(report.State=="Unavailable"&&JointExitStudy.Evaluate(report.Input,report.Rules).Fingerprint==report.Fingerprint&&session.QueryOperation(JsonSerializer.Serialize(request))=="joint-exit-study","后台正式只读查询分块复算，不联网、不把无资料当零或完整");
        try{await session.QueryAsync(JsonSerializer.Serialize(new EtfRequest("joint-exit-study","SSE","510300",To:Day,KnowledgeCutoff:Cutoff)),default);throw new Exception("历史知识截止未拒绝");}catch(InvalidDataException){Check(true,"联合入口拒绝伪造历史知识截止");}
    }
    private sealed class Workspace(JointExitStudyReport report):ResearchWorkspaceStub
    {
        public JointExitStudyReport Report=report;public bool Delayed,Fail;public int Queries;public List<TaskCompletionSource<JointExitStudyReport>> Replies=[];
        public override Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>Task.FromResult(new List<EtfConfiguration>{Report.Input.Configuration,Report.Input.Configuration with{Ticker="510500",Active=false}});
        public override Task<JointExitStudyReport> JointExitStudyAsync(EtfKey key,DateOnly date,CancellationToken token){Queries++;if(Fail)throw new IOException("synthetic unavailable");if(!Delayed)return Task.FromResult(Report);var reply=new TaskCompletionSource<JointExitStudyReport>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(reply);return reply.Task;}
    }
    private sealed class Clock(DateTimeOffset value):TimeProvider{public DateTimeOffset Now=value;public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext{public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoNetwork:IPluginHttpGateway{public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("联合查询禁止联网");}
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);passed++;Console.WriteLine("PASS 联合观察："+text);}
    private static void Reject(Action action,string text){try{action();}catch(InvalidDataException){Check(true,text);return;}throw new Exception("没有拒绝："+text);}
}
