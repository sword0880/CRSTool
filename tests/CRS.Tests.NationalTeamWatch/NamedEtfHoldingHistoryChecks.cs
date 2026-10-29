using System.Text.Json;
using CRS.Plugins.NationalTeam.Jobs;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Presentation;
using CRS.Plugin.Abstractions;
using CRS.Serialization;
using CRS.Worker.Ipc;
using CRS.Worker.Abstractions;
using CRS.Plugins.NationalTeamWatch;

internal static class NamedEtfHoldingHistoryChecks
{
    internal static readonly DateOnly Day=new(2026,10,20),First=new(2026,3,31),Second=new(2026,6,30),Third=new(2026,9,30);
    private static readonly DateTimeOffset Cutoff=new(2026,10,28,10,0,0,TimeSpan.FromHours(8));
    private static readonly EtfKey Key=new("SSE","510300");private static readonly string Hash=new('A',64);
    private const string Url="https://www.sse.com.cn/synthetic-holding-history.pdf";
    private const string Huijin="中央汇金投资有限责任公司",Safe="梧桐树投资平台有限责任公司",Chip="国家集成电路产业投资基金股份有限公司";
    private static int passed;
    public static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);passed=0;
        foreach(var name in InstitutionAccounts.Names)Check(InstitutionAccounts.Group(name.LegalName)==name.Group,"中央目录精确识别："+name.LegalName);
        Check(InstitutionAccounts.Groups.Count==7&&InstitutionAccounts.RequiredFamilies.Count==5,"主体目录供ETF和股票共用，汇金两法人保留独立分组");
        Check(InstitutionAccounts.Group("国家集成电路产业投资基金三期股份有限公司")=="国家大基金"&&InstitutionAccounts.Group("上海集成电路产业投资基金股份有限公司") is null,"国家大基金分期法人精确识别，不把地方基金归入");
        Check(InstitutionAccounts.CandidateGroup("某国家大基金ETF") is null&&InstitutionAccounts.Group("梧桐树投资平台有限责任公司附属账户") is null,"近似名称和产品名不自动认定身份");
        Check(InstitutionAccounts.Group("全国社保基金一零一组合")=="社保基金"&&InstitutionAccounts.Group("基本养老保险基金一零零一组合")=="养老保险基金","既有社保与养老组合仍分开");
        var input=Fixture();var report=NamedEtfHoldingHistory.Evaluate(input);
        Check(report.Fingerprint=="AC1B90A7CF823868FA9EB48AF0477CAA31A9E95ACC73249C028480BBB91EF921","日期精度扩展不改变修改前冻结的准确时刻指纹");
        Publication(input);PublicationStorage(Path.Combine(root,"publication"));await PublicationClient();await PublicationBackground(Path.Combine(root,"publication-background"));
        Check(report.Rows.Single(x=>x.ReportAsOf==Second&&x.LegalName==Huijin) is{Delta:-50,PreviousShares:100,Shares:50,State:"Decreased"},"同一账户相邻已登记报告期正确比较，不当作单日交易");
        var fractional=NamedEtfHoldingHistory.Evaluate(input with{Holdings=input.Holdings.Select(x=>x.Id=="first-a"?x with{Shares=100.12m}:x.Id=="second-a"?x with{Shares=50.10m}:x).ToArray()});
        Check(fractional.Rows.Single(x=>x.ReportAsOf==Second&&x.LegalName==Huijin).Delta==-50.02m,"披露份额和期间差额保持原始小数精度");
        Check(report.Rows.Single(x=>x.ReportAsOf==Second&&x.LegalName==Safe) is{State:"NotDisclosed",Shares:null,Delta:null,PreviousShares:200},"前期账户本期未列示，保留未知而不补零");
        Check(report.Rows.Single(x=>x.ReportAsOf==Third&&x.LegalName==Safe) is{State:"FirstDisclosedPreviousUnknown",Delta:null,PreviousShares:null},"缺失一期后重现不能跨缺口比较");
        Check(report.Baseline is{ReportAsOf:{} date,State:"LatestReviewedDisclosedSubset",Holdings.Count:2}&&date==Third&&report.Baseline.Exclusions.Count==3,"最新子集不混合旧期账户，所有排除记录明确保留");
        Check(!report.WholePortfolioProven&&report.HistoricalKnowledgeProof=="NotProven"&&report.DailyTradingEvidence=="NotObserved"&&report.Families.Single(x=>x.Family=="外汇局投资平台").LatestDisclosedAccounts==1,"分类覆盖只计最新已核子集，完整组合及每日成交未证明");
        Check(report.Families.Single(x=>x.Family=="国家大基金") is{LatestDisclosedAccounts:0,ReportAsOf:null,State:"NoReviewedDisclosureInLatestSubset"},"没有本期国家大基金披露仍为未知，不是零持仓");
        Check(NamedEtfHoldingHistory.Evaluate(input with{Holdings=input.Holdings.Reverse().ToArray()}).Fingerprint==report.Fingerprint,"输入记录排列不影响规范指纹");
        var roundTrip=JsonSerializer.Deserialize<NamedEtfHoldingHistoryReport>(JsonSerializer.Serialize(report))!;
        Check(NamedEtfHoldingHistory.Evaluate(roundTrip.Input).Fingerprint==report.Fingerprint,"跨JSON完整复算输入与结果一致");
        var values=input.Holdings.ToList();var frozen=NamedEtfHoldingHistory.Evaluate(input with{Holdings=values});values.Clear();Check(frozen.Input.Holdings.Count==5,"调用者修改列表不能改变冻结输入");
        var duplicate=input.Holdings[^1] with{Id="duplicate-same-fact",DocumentHash=new('B',64)};
        var repeated=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,duplicate]});
        Check(repeated.Baseline.Holdings.Count==2&&repeated.Baseline.Exclusions.Any(x=>x.Reason=="DuplicateSameFactEvidence")&&repeated.Rows.Single(x=>x.ReportAsOf==Third&&x.LegalName==Safe).EvidenceIds.Count==2,"同账户同一期同值多原件保留引用，只计一次");
        Check(NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,duplicate with{LegalEntityId=" "+duplicate.LegalEntityId+" ",LegalName=" "+duplicate.LegalName+" ",Account=" "+duplicate.Account+" "}]}).Baseline.Holdings.Count==2,"首尾空格不制造另一独立持有人或账户");
        var conflict=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,duplicate with{Shares=999}]});
        Check(conflict.Baseline is{State:"LatestBaselineUnavailable",Holdings.Count:0}&&conflict.Rows.Single(x=>x.ReportAsOf==Third&&x.LegalName==Safe) is{State:"ConflictingDisclosures",Shares:null,Delta:null},"最新期矛盾关闭整个基准，不回退健康旧期或挑选顺眼数值");
        var duplicateLegal=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,duplicate with{LegalEntityId="another-id-for-same-holder"}]});
        Check(duplicateLegal.Baseline.Holdings.Count==0&&duplicateLegal.Rows.Any(x=>x.State=="AmbiguousLegalIdentity"),"同名同账户换一个法人ID不能被当作另一独立持有人重复相加");
        var mixedLegal=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,duplicate with{LegalName=Chip,Account="another-account"}]});
        Check(mixedLegal.Baseline.Holdings.Count==0,"同一法人ID对应不同法人名称不能自动合并或累计");
        var damaged=NamedEtfHoldingHistory.Evaluate(input with{Holdings=input.Holdings.Select(x=>x.HoldingAsOf==Third?x with{OriginalConfirmed=false}:x).ToArray()});
        Check(damaged.Baseline.Holdings.Count==0&&damaged.Rows.Where(x=>x.ReportAsOf==Third&&x.State!="NotDisclosed").All(x=>x.State=="EvidenceUnavailable"),"最新期原件失去确认不使用旧确认或零值");
        var noComparable=NamedEtfHoldingHistory.Evaluate(input with{Metadata=null});Check(noComparable.Rows.All(x=>x.Delta is null),"可比性证据缺失不计算份额差额");
        var renamed=NamedEtfHoldingHistory.Evaluate(input with{Holdings=input.Holdings.Select(x=>x.HoldingAsOf==Second&&x.LegalName==Huijin?x with{LegalName="变更名称"}:x).ToArray()});
        Check(renamed.Rows.Single(x=>x.ReportAsOf==Second&&x.Account=="account-huijin").State=="IdentityChanged","相同法人ID出现名称变化，需要核对而不自动跨名比较");
        var future=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,Holding("future","future-legal",Chip,"account-chip",Day.AddDays(1),1)]});
        Check(future.Baseline.ReportAsOf==Third&&future.Baseline.Exclusions.Any(x=>x.Reason=="ReportAfterAsOf"),"晚于统计日的报告期保留排除原因，不改变当前基准");
        var late=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,Holding("late","late-legal",Chip,"account-chip",Day,1) with{FirstSeenAt=Cutoff.AddSeconds(1)}]});
        Check(late.Baseline.ReportAsOf==Third&&late.Baseline.Exclusions.Any(x=>x.Reason=="KnownAfterCutoff"),"快照之后才首见的证据不能回看使用");
        var unknown=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[Holding("unknown","unknown-legal","合成目录外法人",Safe,Third,100)]});
        Check(unknown.Rows.Single().InstitutionGroup=="主体待核验"&&unknown.Families.All(x=>x.LatestDisclosedAccounts==0)&&unknown.Baseline is{State:"NoRecognizedInstitutionBaseline",Holdings.Count:0},"不借用账户标签替代持有人法人身份，也不把其份额计入国家队基准");
        Check(NamedEtfHoldingHistory.Evaluate(input with{Holdings=[Holding("anonymous","anonymous-legal","未知法人",Safe,Third,100)]}).Baseline.State=="LatestBaselineUnavailable","匿名占位名即使被误填确认，也不能成为健康具名披露");
        var mixed=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,Holding("private","private-legal","合成私募法人","private-account",Third,999999)]});
        Check(mixed.Baseline.Holdings.Count==2&&mixed.Baseline.Exclusions.Any(x=>x.HoldingId=="private"&&x.Reason=="InstitutionIdentityUnresolved")&&mixed.Rows.Any(x=>x.LegalName=="合成私募法人"),"其他具名持有人保留记录及排除原因，不能制造国家队持仓基准");
        var empty=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[]});Check(empty.Rows.Count==0&&empty.Families.Count==5&&empty.Baseline.State=="NoDisclosedBaseline","空库保留五类主体缺口，不伪造零持仓");
        Reject(()=>NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings,input.Holdings[0]]}),"重复修订身份拒绝");
        Reject(()=>NamedEtfHoldingHistory.Evaluate(input with{Holdings=[input.Holdings[0] with{Ticker="510500"}]}),"其他ETF记录不能混入");
        Reject(()=>NamedEtfHoldingHistory.Evaluate(input with{AsOf=DateOnly.FromDateTime(Cutoff.DateTime)}),"未结束统计日拒绝");
        File.WriteAllText(Path.Combine(root,"history.json"),JsonSerializer.Serialize(report));File.WriteAllText(Path.Combine(root,"conflict.json"),JsonSerializer.Serialize(conflict));File.WriteAllText(Path.Combine(root,"empty.json"),JsonSerializer.Serialize(empty));
        await Model(report);Storage(Path.Combine(root,"storage"));Equity(Path.Combine(root,"equity"));await Background(Path.Combine(root,"background"));
        File.WriteAllText(Path.Combine(root,"acceptance.json"),JsonSerializer.Serialize(new{Passed=passed,Installed=false,Source="SyntheticNamedHoldingHistory",WholePortfolioProven=false},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static NamedEtfHistoryInput Fixture()=>new(Key,Day,Cutoff,new(Key.Market,Key.Ticker,"synthetic-fund","synthetic-manager","synthetic-index",new(2026,1,1),Day,Url,Hash,"synthetic-reviewer",true),[
        Holding("first-a","legal-huijin",Huijin,"account-huijin",First,100),Holding("first-b","legal-safe",Safe,"account-safe",First,200),
        Holding("second-a","legal-huijin",Huijin,"account-huijin",Second,50),Holding("third-a","legal-huijin",Huijin,"account-huijin",Third,40),Holding("third-b","legal-safe",Safe,"account-safe",Third,150)]);
    private static NamedEtfHolding Holding(string id,string legal,string name,string account,DateOnly day,decimal shares)=>new(id,Key.Market,Key.Ticker,legal,name,account,day,Cutoff.AddDays(-2),Cutoff.AddDays(-1),shares,Url,Hash,"合成P10","synthetic-reviewer",true,true);
    private static void Publication(NamedEtfHistoryInput input)
    {
        var dated=input.Holdings[^1] with{PublishedAt=null,PublicationDate=new(2026,10,26),PublicationPrecision=EtfPublicationPrecision.DateOnly};
        Check(ExitAlertEvaluator.NamedVerified(dated)&&dated.PublishedAt is null&&dated.PublicationText.Contains("仅日期")&&!dated.PublicationText.Contains("00:00"),"日期精度保留真实日期，不补造零点或准确时刻");
        Check(!EtfHoldingPublication.KnownAfter(dated,dated.FirstSeenAt)&&EtfHoldingPublication.KnownAfter(dated,dated.FirstSeenAt.AddTicks(-1)),"日期证据只在真实系统首见后可用，边界前一刻仍未知");
        var overnight=dated with{PublicationDate=new(2026,10,28),FirstSeenAt=new(2026,10,27,16,30,0,TimeSpan.Zero)};
        Check(ExitAlertEvaluator.NamedVerified(overnight)&&EtfHoldingPublication.KnownAfter(overnight,new(2026,10,27,23,59,59,TimeSpan.FromHours(8))),"UTC跨北京时间午夜不提前使用次日公告");
        foreach(var invalid in new[]{dated with{PublicationDate=null},dated with{PublicationDate=default(DateOnly)},dated with{PublicationDate=new(2026,10,28)},
            dated with{PublicationDate=Third.AddDays(-1)},dated with{PublishedAt=Cutoff.AddDays(-2)},dated with{PublicationPrecision=(EtfPublicationPrecision)9},
            dated with{PublicationPrecision=EtfPublicationPrecision.ExactTime,PublicationDate=null},dated with{IndependentOwnershipConfirmed=false}})
            Check(!ExitAlertEvaluator.NamedVerified(invalid),"缺失、矛盾、未来日期或持有关系不足均关闭具名核验");
        var datedHistory=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings.SkipLast(1),dated]});
        Check(datedHistory.Baseline.Holdings.Count==2&&datedHistory.Fingerprint!=NamedEtfHoldingHistory.Evaluate(input).Fingerprint,"日期精度参与冻结指纹，已核日期可保留披露子集");
        var invalidLatest=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings.SkipLast(1),dated with{PublicationDate=null}]});
        Check(invalidLatest.Baseline is{State:"LatestBaselineUnavailable",Holdings.Count:0},"最新期缺少公布资料时不回退旧期或忽略损坏记录");
        var future=NamedEtfHoldingHistory.Evaluate(input with{Holdings=[..input.Holdings.SkipLast(1),dated with{FirstSeenAt=Cutoff.AddTicks(1)}]});
        Check(future.Baseline.Exclusions.Any(x=>x.HoldingId==dated.Id&&x.Reason=="KnownAfterCutoff"),"日期精度不绕过截止之后的真实首见");
    }
    private static void PublicationStorage(string root)
    {
        var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);var data=Path.Combine(root,"data");
        using var repo=TestMarketData.Open(data,raw,timeProvider:new Clock());
        repo.RegisterAlertDocument(new(Key.Market,Key.Ticker,Url),"%PDF-synthetic-publication-only"u8.ToArray());var document=repo.AlertDocuments(Key).Single();
        var review=new NamedEtfHoldingReview(document.Id,"synthetic-date-legal",Huijin,"synthetic-date-account",Third,null,150,"合成P1","synthetic-reviewer",true,true)
            {PublicationDate=new(2026,10,27),PublicationPrecision=EtfPublicationPrecision.DateOnly};
        repo.ReviewNamedEtfHolding(review);repo.ReviewNamedEtfHolding(review);
        var row=repo.NamedEtfHoldings(Key).Single();
        Check(row is{PublishedAt:null,PublicationPrecision:EtfPublicationPrecision.DateOnly}&&row.PublicationDate==review.PublicationDate&&row.FirstSeenAt==Cutoff&&ExitAlertEvaluator.NamedVerified(row),"实际普通SQLite保留日期精度、空时刻及后台固定首见，重复核验幂等");
        foreach(var invalid in new[]{review with{PublicationDate=null},review with{PublicationDate=new(2026,10,29)},review with{PublishedAt=Cutoff.AddDays(-1)},review with{IndependentOwnershipConfirmed=false}})
            Reject(()=>repo.ReviewNamedEtfHolding(invalid),"持久核验拒绝缺失、未来、混填或独立关系未核");
        Check(repo.NamedEtfHoldings(Key).Count==1,"失败审核不覆盖已保存日期事实");
        File.WriteAllText(Path.Combine(rawPath,document.Hash+".raw"),"damaged");
        Check(!repo.NamedEtfHoldings(Key).Single().OriginalConfirmed,"日期精度不跳过当前原件即时损坏核验");
        var legacyPath=Path.Combine(root,"legacy");var legacyRaw=new RawEvidenceStore(Path.Combine(legacyPath,"raw"));
        using var legacy=TestMarketData.Open(Path.Combine(legacyPath,"data"),legacyRaw,timeProvider:new Clock());
        using var fixture=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","EtfHoldingPublicationLegacy.json")));
        var payload=fixture.RootElement.GetProperty("Payload").GetString()!;var old=JsonSerializer.Deserialize<NamedEtfHolding>(payload)!;
        Check(fixture.RootElement.GetProperty("SyntheticOnly").GetBoolean()&&JsonSerializer.Serialize(old)==payload,"修改前保存的旧JSON逐字节回读一致，不自动加日期或精度字段");
        legacy.RegisterAlertDocument(new(old.Market,old.Ticker,old.DocumentUrl),System.Text.Encoding.UTF8.GetBytes(fixture.RootElement.GetProperty("RawPdfText").GetString()!));
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(legacyPath,"data","market.db"),Pooling=false}.ToString()))
        {
            db.Open();using var command=db.CreateCommand();command.CommandText="INSERT INTO nt_named_etf_holding VALUES($id,$m,$t,$p)";
            command.Parameters.AddWithValue("$id",old.Id);command.Parameters.AddWithValue("$m",old.Market);command.Parameters.AddWithValue("$t",old.Ticker);command.Parameters.AddWithValue("$p",payload);command.ExecuteNonQuery();
        }
        var original=legacy.AlertDocuments(Key).Single();
        legacy.ReviewNamedEtfHolding(new(original.Id,old.LegalEntityId,old.LegalName,old.Account,old.HoldingAsOf,old.PublishedAt,old.Shares,old.EvidencePage,old.Reviewer,true,true));
        Check(legacy.NamedEtfHoldings(Key).Single()==old&&old.PublicationPrecision==EtfPublicationPrecision.ExactTime,"旧表内准确时刻记录保持原值，复核不触发不可变事实误冲突");
    }
    private static async Task PublicationClient()
    {
        var control=new PublicationControl();var name="CRS.Holding.Publication."+Guid.NewGuid().ToString("N");
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(20));var server=new BackgroundPipeServer(control,name).RunAsync(stop.Token);
        var review=new NamedEtfHoldingReview(new('A',64),"synthetic-legal",Huijin,"synthetic-account",Third,null,1,"合成P1","synthetic-reviewer",true,true)
            {PublicationDate=new(2026,10,27),PublicationPrecision=EtfPublicationPrecision.DateOnly};
        try
        {
            var wire=EtfBusinessProtocol.WriteRequest(new("alert-holding-review-date-v1",NamedEtfHolding:review));
            Check(wire.Contains("\"PublishedAt\":null",StringComparison.Ordinal)&&EtfBusinessProtocol.ParseRequest(wire).NamedEtfHolding?.PublishedAt is null,"日期请求显式保存空时刻，严格构造参数校验仍可回读");
            foreach(var mode in new[]{"Old","NoFeature","BadVersion"})
            {
                control.Mode=mode;
                try{await new WorkerEtfWorkspace(new(name)).ReviewNamedEtfHoldingAsync(review,stop.Token);throw new Exception("旧后台或能力异常被放行。");}
                catch(InvalidDataException){Check(control.Writes.Count==0,"能力协商拒绝日期写入且不降级旧操作："+mode);}
            }
            control.Mode="Supported";var reader=new WorkerEtfWorkspace(new(name));await reader.ReviewNamedEtfHoldingAsync(review,stop.Token);
            Check(control.Writes.Single().Operation=="alert-holding-review-date-v1"&&control.Writes.Single().NamedEtfHolding is{PublishedAt:null,PublicationPrecision:EtfPublicationPrecision.DateOnly},"真实客户端经新操作发送日期精度，不创建伪时刻");
            try{await reader.ReviewNamedEtfHoldingAsync(review with{PublishedAt=Cutoff},stop.Token);throw new Exception("混填可发送。");}catch(InvalidDataException){Check(control.Writes.Count==1,"混填在客户端发送前拒绝");}
            control.Mode="Old";await new WorkerEtfWorkspace(new(name)).ReviewNamedEtfHoldingAsync(review with{PublishedAt=Cutoff.AddDays(-1),PublicationDate=null,PublicationPrecision=EtfPublicationPrecision.ExactTime},stop.Token);
            Check(control.Writes.Last().Operation=="alert-holding-review"&&!control.LastPayload!.Contains("PublicationPrecision",StringComparison.Ordinal)&&!control.LastPayload.Contains("PublicationDate",StringComparison.Ordinal),"旧准确时刻操作保留旧载荷，不新增能力要求");
        }
        finally{stop.Cancel();try{await server;}catch(OperationCanceledException){}}
    }
    private sealed class PublicationControl:IBackgroundControl
    {
        internal string Mode="Old";internal string? LastPayload;internal List<EtfRequest> Writes=[];
        public Task<BackgroundResponse> HandleAsync(BackgroundRequest request,BackgroundCaller caller,CancellationToken token)
        {
            var query=EtfBusinessProtocol.ParseRequest(request.Payload!);
            if(request.Command=="QueryPlugin")
            {
                if(Mode=="Old")return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"UnknownQuery"));
                var cap=new EtfQueryCapabilities(Mode=="BadVersion"?2:1,Mode=="Supported"?[EtfQueryCapabilities.PublicationDateHoldings]:[],256);
                return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Ok",Payload:JsonSerializer.Serialize(new EtfQueryPage(JsonSerializer.Serialize(new[]{cap}),Guid.NewGuid().ToString("N"),null))));
            }
            if(request.Command!="RunNow")throw new Exception("出现未列测试命令。");
            Writes.Add(query);LastPayload=request.Payload;
            return Task.FromResult(new BackgroundResponse(WorkerProtocol.Version,request.RequestId,"Succeeded",Receipt:new(request.RequestId,"Completed","Succeeded","synthetic-date-execution")));
        }
    }
    private static async Task PublicationBackground(string root)
    {
        AlertDocument document;
        using(var repo=TestMarketData.Open(Path.Combine(root,"data"),new RawEvidenceStore(Path.Combine(root,"raw")),timeProvider:new Clock()))
        {repo.RegisterAlertDocument(new(Key.Market,Key.Ticker,Url),"%PDF-synthetic-backend-date"u8.ToArray());document=repo.AlertDocuments(Key).Single();}
        await using var session=new NationalTeamBackgroundSession(new Context(root),new NoNetwork());await session.StartAsync(default);
        var review=new NamedEtfHoldingReview(document.Id,"synthetic-backend-date",Huijin,"synthetic-account",Third,null,1,"合成P1","synthetic-reviewer",true,true)
            {PublicationDate=new(2026,10,27),PublicationPrecision=EtfPublicationPrecision.DateOnly};
        var result=await session.ExecuteAsync("alert-manage",EtfBusinessProtocol.WriteRequest(new("alert-holding-review-date-v1",NamedEtfHolding:review)),default,Guid.NewGuid());
        Check(result.Status=="Succeeded","实际后台管理入口接受独立日期核验操作");
        var denied=await session.ExecuteAsync("alert-manage",EtfBusinessProtocol.WriteRequest(new("alert-holding-review",NamedEtfHolding:review)),default,Guid.NewGuid());
        Check(denied.Status=="Rejected","实际旧操作拒绝日期字段，不能忽略精度降级保存");
        var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(EtfBusinessProtocol.WriteRequest(new("alert-holdings",Key.Market,Key.Ticker)),default));
        var value=JsonSerializer.Deserialize<NamedEtfHolding[]>(page.Items)!.Single();
        Check(value.PublishedAt is null&&value.FirstSeenAt==Cutoff&&value.PublicationDate==review.PublicationDate,"后台回读保留空时刻及固定首见，不采信客户端猜测的时点");
    }
    private static async Task Model(NamedEtfHoldingHistoryReport report)
    {
        var workspace=new Workspace(report);var model=new EtfHoldingHistoryViewModel(workspace,default){Fund=new("SSE","510300","合成ETF","fixture",true,"fixture","fixture",1),AsOf=Day.ToDateTime(TimeOnly.MinValue)};
        await model.ReadAsync();Check(model.Rows.Count==report.Rows.Count&&model.Families.Count==5&&model.SelectionDetails.Count==3,"页面仅映射后台变化、五类覆盖及排除原因");
        model.AsOf=model.AsOf.Value.AddDays(-1);Check(model.Report is null&&model.Rows.Count==0,"条件改变立即移除旧结果");model.AsOf=Day.ToDateTime(TimeOnly.MinValue);
        workspace.Delayed=true;var first=model.ReadAsync();var second=model.ReadAsync();workspace.Replies[1].SetResult(report);await second;workspace.Replies[0].SetResult(report with{Rows=[]});await first;Check(model.Rows.Count==report.Rows.Count&&!model.Busy,"旧请求不能覆盖新结果");
        var cancel=model.ReadAsync();model.Cancel();workspace.Replies[^1].SetResult(report);await cancel;Check(model.Report is null&&!model.Busy,"主动取消后迟到结果不展示");
        workspace.Delayed=false;workspace.Fail=true;await model.ReadAsync();Check(model.Report is null&&model.Status.Contains("不可用"),"查询失败不保留旧持仓");
    }
    private static void Storage(string root)
    {
        var rawPath=Path.Combine(root,"raw");var raw=new RawEvidenceStore(rawPath);var data=Path.Combine(root,"data");using var repo=TestMarketData.Open(data,raw,timeProvider:new Clock());
        var documents=new List<AlertDocument>();
        foreach(var (period,shares) in new[]{(First,100m),(Second,80m)})
        {
            repo.RegisterAlertDocument(new(Key.Market,Key.Ticker,Url+"?period="+period),System.Text.Encoding.UTF8.GetBytes("%PDF-synthetic-history-"+period));var document=repo.AlertDocuments(Key).Single(x=>x.Url.EndsWith(period.ToString(),StringComparison.Ordinal));documents.Add(document);
            repo.ReviewNamedEtfHolding(new(document.Id,"legal-safe",Safe,"account-safe",period,Cutoff.AddDays(-1),shares,"合成P10","synthetic-reviewer",true,true));
        }
        repo.ReviewAlertMetadata(new(documents[^1].Id,"synthetic-fund","synthetic-manager","synthetic-index",new(2026,1,1),Day,"synthetic-reviewer",true));
        var history=repo.Read(r=>r.EtfHoldingHistory(Key,Day),default);
        Check(history.Rows.First().Delta==-20&&history.Input.SnapshotCutoff==Cutoff,"实际普通SQLite审核资料在同一快照生成变化");
        Check(repo.AlertEvents(new()).Count==0&&repo.AlertOutbox().Count==0,"读取历史不创建金融事件或通知");
        File.WriteAllText(Path.Combine(rawPath,documents[^1].Hash+".raw"),"damaged");var damaged=repo.Read(r=>r.EtfHoldingHistory(Key,Day),default);
        Check(damaged.Baseline.State=="LatestBaselineUnavailable"&&damaged.Rows.First().Shares is null,"实际原件损坏时撤去最新基准，不回退旧期");
        Reject(()=>repo.Read(r=>r.EtfHoldingHistory(new("SSE","000000"),Day),default),"未配置证券拒绝");
    }
    private static void Equity(string root)
    {
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"));using var repo=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:new Clock());var key=new EtfKey("SSE","600036");
        var names=InstitutionAccounts.Names.Where(x=>x.Group is "外汇局投资平台" or "国家大基金").Select(x=>x.LegalName).ToArray();
        repo.SaveEquityDisclosure(key.Market,key.Ticker,OfficialEquitySource.Request(key.Market,key.Ticker),DirectEquityChecks.Source(new(2026,10,16),names.Select(n=>(n,100m,1m)).ToArray()));
        var disclosure=repo.EquityDisclosures(key).Single();
        foreach(var name in names)repo.ReviewEquity(new(disclosure.Id,Third,new(2026,10,16),Url,"合成P10","synthetic-reviewer",false,"Unknown",Account:name,OriginalConfirmed:true),"%PDF-synthetic-new-institutions"u8.ToArray());
        var rows=repo.EquityHoldings(null,null,null,false);
        Check(rows.Count(x=>x.Institution=="外汇局投资平台")==1&&rows.Count(x=>x.Institution=="国家大基金")==3&&rows.All(x=>x.Nature=="Unknown"&&x.Delta is null),"实际股票审核支持外汇局及大基金三期，名称识别不自动批准投资性质或期间变化");
    }
    private static async Task Background(string root)
    {
        await using var session=new NationalTeamBackgroundSession(new Context(root),new NoNetwork());await session.StartAsync(default);
        var request=new EtfRequest("etf-holding-history",Key.Market,Key.Ticker,To:Day,Count:1);var blocks=new List<JsonDocumentBlock>();
        while(true){var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(request),default));blocks.AddRange(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(page.Items)!);if(page.NextOffset is not {} next)break;request=request with{Offset=next,Snapshot=page.Snapshot};}
        var report=JsonDocumentBlocks.Decode<NamedEtfHoldingHistoryReport>(blocks,default);Check(report.Families.Count==5&&report.Rows.Count==0&&session.QueryOperation(JsonSerializer.Serialize(request))=="etf-holding-history","正式后台分块查询保留空库缺口，不联网或批准事实");
    }
    private sealed class Workspace(NamedEtfHoldingHistoryReport report):ResearchWorkspaceStub
    {
        public bool Delayed,Fail;public List<TaskCompletionSource<NamedEtfHoldingHistoryReport>> Replies=[];
        public override Task<NamedEtfHoldingHistoryReport> EtfHoldingHistoryAsync(EtfKey key,DateOnly asOf,CancellationToken token){if(Fail)throw new IOException("synthetic unavailable");if(!Delayed)return Task.FromResult(report);var value=new TaskCompletionSource<NamedEtfHoldingHistoryReport>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(value);return value.Task;}
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Cutoff;}
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext{public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoNetwork:IPluginHttpGateway{public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)=>throw new Exception("历史读取禁止联网");}
    private static void Check(bool condition,string text){if(!condition)throw new Exception(text);passed++;Console.WriteLine("PASS 具名ETF历史："+text);}
    private static void Reject(Action action,string text){try{action();}catch(InvalidDataException){Check(true,text);return;}throw new Exception("没有拒绝："+text);}
}
