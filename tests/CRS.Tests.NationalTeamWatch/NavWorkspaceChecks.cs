using System.Text;
using System.Text.Json;
using CRS.Serialization;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>真实仓储跨模块快照、完整文档传输及界面迟到结果隔离；不访问官网或真实服务。</summary>
internal static class NavWorkspaceChecks
{
    private static readonly EtfKey Key=new("SZSE","159915");
    private static readonly DateOnly From=new(2026,10,6),Through=new(2026,10,10);
    private static int passed;
    private sealed class Clock:TimeProvider
    {public DateTimeOffset At=new(2026,10,26,12,0,0,TimeSpan.Zero);public override DateTimeOffset GetUtcNow()=>At;}
    internal static async Task RunAsync(string root)
    {
        passed=0;Blocks();var report=Store(Path.Combine(root,"store"));await Endpoint(Path.Combine(root,"store"));await Presentation(report);
        File.WriteAllText(Path.Combine(root,"frozen-nav-study.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS NAV工作区合计{passed}；真实隔离仓储/DTO/界面状态，不代表官网长期覆盖或市场择时有效。");
    }
    private static void Blocks()
    {
        var document=new string('研',20000);var parts=JsonDocumentBlocks.Encode(document,default);
        Check(parts.Count>1&&JsonDocumentBlocks.Decode<string>(parts,default)==document,"多块UTF8研究文档完整复原，不因中文跨字节边界截断");
        Reject(()=>JsonDocumentBlocks.Decode<string>(parts.Reverse().ToArray(),default),"乱序块拒绝");
        Reject(()=>JsonDocumentBlocks.Decode<string>(parts.Take(parts.Count-1).ToArray(),default),"缺末块拒绝半份研究");
        var damaged=parts.ToArray();damaged[0]=damaged[0] with{Content=damaged[0].Content.ToArray()};damaged[0].Content[0]^=1;
        Reject(()=>JsonDocumentBlocks.Decode<string>(damaged,default),"原始内容变化但声明哈希不变仍被拒绝");
        var mixed=parts.ToArray();mixed[1]=mixed[1] with{Sha256=new('A',64)};
        Reject(()=>JsonDocumentBlocks.Decode<string>(mixed,default),"混入另一文档身份拒绝");
        Reject(()=>JsonDocumentBlocks.Encode(document,default,100),"序列化途中超过预算即拒绝");
        Reject(()=>JsonDocumentBlocks.Decode<string>([parts[0] with{FormatVersion=2}],default),"未知文档格式不降级");
        try{JsonDocumentBlocks.Decode<string>(parts,new(true));throw new Exception("忽略取消");}catch(OperationCanceledException){Check(true,"组装文档保留取消语义");}
    }
    private static EtfNavStudyReport Store(string root)
    {
        var clock=new Clock();var reads=new System.Collections.Concurrent.ConcurrentBag<string>();
        var raw=new RawEvidenceStore(Path.Combine(root,"raw"),openRead:path=>{reads.Add(Path.GetFileNameWithoutExtension(path));return File.OpenRead(path);});
        using var repository=TestMarketData.Open(Path.Combine(root,"data"),raw,timeProvider:clock);
        var warm=From.AddDays(-90);var days=Enumerable.Range(0,Through.DayNumber-warm.DayNumber+1).Select(warm.AddDays).ToArray();
        var fetch=repository.RecordFetch(raw.Save("synthetic registered calendar and ETF observations"u8.ToArray()),"https://www.szse.cn/nav-workspace-fixture");
        repository.ImportCalendar(fetch,days.Select(d=>("SZSE",d)).ToArray(),default);
        repository.ImportShares(fetch,Enumerable.Range(0,10).Select(i=>new ImportRow(Key,new DateOnly(2026,10,1).AddDays(i),100+i,"份")).ToArray(),default);
        const string url="https://www.szse.cn/nav-study-comparability.pdf";
        repository.RegisterAlertDocument(new(Key.Market,Key.Ticker,url),"%PDF-synthetic-comparability"u8.ToArray());
        repository.ReviewAlertMetadata(new(repository.AlertDocuments(Key).Single().Id,"fund-fixture","manager-fixture","index-fixture",warm,Through,"synthetic-reviewer",true));
        EtfNavStudyReport Query()=>((IMarketReadSessions)repository).Read(r=>EtfNavStudy.Run(r.FreezeEtfNavStudy(Key,From,Through)),default);
        try{repository.FreezeEtfNavStudy(Key,From,Through);throw new Exception("写会话可冻结研究");}
        catch(InvalidOperationException){Check(true,"跨模块研究强制在独立只读快照中取得");}
        var empty=Query();
        Check(empty.Windows.Any(x=>x.DeltaShares==1&&x.State=="NAV Unavailable")&&empty.Windows.All(x=>x.EstimatedAmountYuan is null),"没有NAV时保留份额差额，金额未知不伪造零");
        repository.ImportNavDocuments(NavResearchChecks.Fixture());clock.At=clock.At.AddSeconds(1);
        var other=new EtfKey("SSE","510300");
        repository.RegisterAlertDocument(new(other.Market,other.Ticker,"https://www.sse.com.cn/other-comparability.pdf"),"%PDF-unrelated-comparability"u8.ToArray());
        var unrelated=repository.AlertDocuments(other).Single();
        repository.ReviewAlertMetadata(new(unrelated.Id,"fund-other","manager-other","index-other",warm,Through,"synthetic-reviewer",true));reads.Clear();
        var report=Query();var last=report.Windows.Where(x=>x.Date==Through).ToArray();
        Check(!reads.Contains(unrelated.Hash),"单基金研究不读取其他ETF可比性原件，避免无关资料拖累查询");
        Check(last.Single(x=>x.Window==1) is{DeltaShares:1,EstimatedAmountYuan:1.09m,State:"Complete"}
            &&last.Single(x=>x.Window==5) is{DeltaShares:5,EstimatedAmountYuan:5.35m,State:"Complete"},"正式NAV与份额按每日同日单位净值组合，得到1/5日金额");
        Check(last.Single(x=>x.Window==20).EstimatedAmountYuan is null,"前置份额不足时20日窗口不可计算");
        Check(report.FrozenInputs.NavRevisions is{Count:10}&&report.FrozenInputs.Nav.Sources.Count==4&&report.FrozenInputs.Nav.Sources.All(x=>x.Bytes is null),"保留NAV修订与四原件身份，未记录长度保持未知");
        var reassembled=JsonDocumentBlocks.Decode<EtfNavStudyReport>(JsonDocumentBlocks.Encode(report,default),default);
        Check(reassembled.Fingerprint==report.Fingerprint&&EtfNavStudy.VerifyFrozen(reassembled).Fingerprint==report.Fingerprint,"完整文档穿过分块DTO后仍可独立数学复算同一指纹");
        Reject(()=>EtfNavStudy.VerifyFrozen(report with{AlgorithmVersion="etf-nav-study-v1"}),"旧研究版本明确拒绝，不以当前输入合同静默重算");
        Reject(()=>EtfNavStudy.VerifyFrozen(report with{Windows=report.Windows.Select((x,i)=>i==0?x with{EstimatedAmountYuan=999m}:x).ToArray()}),"即使输入指纹未变，篡改窗口金额也不能通过数学复算");
        Check(report is{TimingProof:"NotProven",InstitutionTradeState:"NotObserved"},"研究不升级为已验证择时或机构成交");
        var original=NavResearchChecks.Fixture()[3];var rawPath=Path.Combine(root,"raw",original.Sha256+".raw");
        File.WriteAllText(rawPath,"tampered");Reject(()=>Query(),"必需NAV原件损坏时整份研究失败关闭");File.WriteAllBytes(rawPath,original.Content);
        repository.ImportNavDocuments(NavStorageChecks.Corrected());clock.At=clock.At.AddSeconds(1);
        var pending=Query();Check(pending.FrozenInputs.NavRevisions!.Any(x=>x.State=="NeedsReview")
            &&pending.Windows.Where(x=>x.Date==Through).All(x=>x.EstimatedAmountYuan is null),"待审更正阻断末日金额，不用旧接受版本绕过");
        var id=pending.FrozenInputs.NavRevisions!.Single(x=>x.State=="NeedsReview").Id;
        repository.ReviewNavRevision(id,true,"synthetic-reviewer");clock.At=clock.At.AddSeconds(1);
        var revised=Query();Check(revised.Fingerprint!=report.Fingerprint&&revised.Windows.Single(x=>x.Date==Through&&x.Window==1).EstimatedAmountYuan==1.0901m,"人工接受更正后新研究使用新值和新身份");
        Check(EtfNavStudy.Run(report.FrozenInputs).Fingerprint==report.Fingerprint,"后续修订不会改变先前已冻结的数学输入");
        Reject(()=>((IMarketReadSessions)repository).Read(r=>r.FreezeEtfNavStudy(Key,From,Through.AddYears(3)),default),"未来或过长研究日期拒绝");
        return report;
    }
    private static async Task Presentation(EtfNavStudyReport report)
    {
        var fixture=new Workspace();var model=new NavStudyViewModel(fixture,fixture,default);await model.EnsureInitializedAsync();
        model.From=From.ToDateTime(TimeOnly.MinValue);model.Through=Through.ToDateTime(TimeOnly.MinValue);
        var request=model.QueryCommand.ExecuteAsync(null);fixture.Replies[^1].SetResult(report);await request;
        Check(model.Rows.Count==report.Windows.Count&&model.Sources.Count==4&&model.CanCopy,"界面呈现完整后台窗口与原件身份");
        request=model.QueryCommand.ExecuteAsync(null);model.Selected=fixture.Funds[1];fixture.Replies[^1].SetResult(report);await request;
        Check(model.Rows.Count==0&&model.Revisions.Count==0&&!model.CanCopy,"切换ETF后迟到响应不能重新写入旧结果");
        model.Selected=fixture.Funds[0];request=model.QueryCommand.ExecuteAsync(null);model.From=model.From!.Value.AddDays(1);fixture.Replies[^1].SetResult(report);await request;
        Check(model.Rows.Count==0&&model.Sources.Count==0,"切换日期后迟到响应不能覆盖当前条件");
        model.From=From.ToDateTime(TimeOnly.MinValue);request=model.QueryCommand.ExecuteAsync(null);fixture.Replies[^1].SetException(new IOException("synthetic unavailable"));await request;
        Check(model.Rows.Count==0&&model.Status.Contains("不可用")&&!model.Busy,"读取失败明确不可用，清理旧结果及忙状态");
    }
    private static async Task Endpoint(string root)
    {
        var gateway=new NoHttp();await using var session=new CRS.Plugins.NationalTeam.Jobs.NationalTeamBackgroundSession(new Context(root),gateway);
        await session.StartAsync(default);
        var request=new EtfRequest("nav-study",Key.Market,Key.Ticker,From,Through,Count:2);var blocks=new List<JsonDocumentBlock>();var pages=0;
        Check(session.QueryOperation(JsonSerializer.Serialize(request))=="nav-study","研究查询进入真实后台诊断白名单");
        while(true)
        {
            var page=EtfBusinessProtocol.ReadResponse<EtfQueryPage>(await session.QueryAsync(JsonSerializer.Serialize(request),default));
            blocks.AddRange(JsonSerializer.Deserialize<List<JsonDocumentBlock>>(page.Items)!);pages++;
            if(page.NextOffset is not {} next)break;
            if(pages==1)
            {
                try{await session.QueryAsync(JsonSerializer.Serialize(request with{Ticker="159919",Snapshot=page.Snapshot,Offset=next}),default);throw new Exception("快照串基金");}
                catch(ArgumentException){Check(true,"实际分页快照绑定证券与日期，拒绝切换证券续读");}
            }
            request=request with{Offset=next,Snapshot=page.Snapshot};
        }
        var report=JsonDocumentBlocks.Decode<EtfNavStudyReport>(blocks,default);
        Check(pages>1&&report.Windows.Single(x=>x.Date==Through&&x.Window==1).EstimatedAmountYuan==1.0901m
            &&EtfNavStudy.Run(report.FrozenInputs).Fingerprint==report.Fingerprint,"后台应用查询多页传回实际更正后金额与完整可复算快照");
        Check(gateway.Calls==0,"研究查询不发HTTP，不触发重新采集或通知");
    }
    private sealed class Context(string root):CRS.Worker.Abstractions.IBackgroundHostContext
    {public string PluginId=>"crs.national-team";public string DataDirectory=>root;public byte[] OpenDataKey()=>TestMarketData.Key.ToArray();}
    private sealed class NoHttp:CRS.Plugin.Abstractions.IPluginHttpGateway
    {
        internal int Calls;
        public Task<CRS.Plugin.Abstractions.PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)
        {Calls++;throw new InvalidOperationException("研究查询不允许联网。");}
    }
    private sealed class Workspace:ResearchWorkspaceStub
    {
        internal EtfConfiguration[] Funds=[new("SZSE","159915","合成创业板ETF","fixture",true,"fixture","fixture",0),new("SSE","510300","合成沪深300ETF","fixture",true,"fixture","fixture",0)];
        internal List<TaskCompletionSource<EtfNavStudyReport>> Replies=[];
        public override Task<List<EtfConfiguration>> ConfigurationAsync(CancellationToken token)=>Task.FromResult(Funds.ToList());
        public override Task<EtfNavStudyReport> NavStudyAsync(EtfKey key,DateOnly from,DateOnly through,CancellationToken token)
        {var next=new TaskCompletionSource<EtfNavStudyReport>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(next);return next.Task;}
    }
    private static void Reject(Action action,string message)
    {try{action();throw new Exception("应当拒绝："+message);}catch(InvalidDataException){Check(true,message);}}
    private static void Check(bool value,string message)
    {if(!value)throw new Exception(message);passed++;Console.WriteLine("PASS NAV工作区："+message);}
}
