using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Presentation;

internal static class EtfHolderCandidateUiChecks
{
    public static async Task RunAsync()
    {
        var workspace=new Workspace();var model=new ExitAlertViewModel(workspace,default);
        model.ProofFund=new("SSE","510300","合成基金","fixture",true,"fixture","fixture",0);await model.ProofLoadTask;
        var first=new AlertDocument(new('A',64),"SSE","510300","https://www.sse.com.cn/first.pdf",new('B',64),DateTimeOffset.UtcNow.ToString("O"));
        var next=first with{Id=new('C',64),Hash=new('D',64),Url="https://www.sse.com.cn/next.pdf"};
        model.ProofDocument=first;var query=model.ReadHolderCandidatesCommand.ExecuteAsync(null);
        model.ProofDocument=next;workspace.Replies[^1].SetResult(Report(first));await query;
        Check(model.HolderCandidates.Count==0&&model.HoldingAccount=="","切换原件后迟到候选不能覆盖当前草稿");
        query=model.ReadHolderCandidatesCommand.ExecuteAsync(null);workspace.Replies[^1].SetResult(Report(next));await query;
        Check(model.HolderCandidates.Count==1&&model.HoldingAccount==""&&!model.HoldingConfirmed&&!model.IndependentOwnershipConfirmed,"展示候选不自动填草稿或确认");
        model.HolderCandidate=model.HolderCandidates.Single();model.UseHolderCandidateCommand.Execute(null);
        Check(model.HoldingAccount=="合成登记持有人"&&model.HoldingShares==123&&model.LegalEntityId==""&&model.PublishedAt==""
            &&!model.HoldingConfirmed&&!model.IndependentOwnershipConfirmed,"显式填草稿仍不补造法人标识、公告时点或审核");
        model.HoldingConfirmed=true;model.IndependentOwnershipConfirmed=true;
        query=model.ReadHolderCandidatesCommand.ExecuteAsync(null);workspace.Replies[^1].SetException(new IOException("synthetic unavailable"));await query;
        Check(model.HolderCandidates.Count==0&&model.HoldingAccount==""&&!model.HoldingConfirmed&&!model.IndependentOwnershipConfirmed&&model.HolderCandidateStatus.Contains("不可用"),"重读失败清空候选、草稿和原确认");
        query=model.ReadHolderCandidatesCommand.ExecuteAsync(null);workspace.Replies[^1].SetResult(Report(first));await query;
        Check(model.HolderCandidates.Count==0&&model.HolderCandidateStatus.Contains("不可用"),"返回另一原件候选必须拒绝");
    }
    private static EtfHolderCandidateReport Report(AlertDocument doc)=>new(doc,"huatai300-holders-pdf-v1","NeedsReview","合成候选",
        new(2025,12,31),new(2026,3,31),75,"份","ListedFundUnits","TopTenExcludingFeederPlusFeeder",[new(1,"合成登记持有人","合成登记\n持有人",123,1,"合成第1页",null)]);
    private sealed class Workspace:ResearchWorkspaceStub
    {
        internal List<TaskCompletionSource<EtfHolderCandidateReport>> Replies=[];
        public override Task<List<AlertDocument>> AlertDocumentsAsync(EtfKey key,CancellationToken token)=>Task.FromResult(new List<AlertDocument>());
        public override Task<List<AlertFundMetadata>> AlertMetadataAsync(CancellationToken token)=>Task.FromResult(new List<AlertFundMetadata>());
        public override Task<List<NamedEtfHolding>> NamedEtfHoldingsAsync(EtfKey key,CancellationToken token)=>Task.FromResult(new List<NamedEtfHolding>());
        public override Task<EtfHolderCandidateReport> EtfHolderCandidatesAsync(EtfKey key,string documentId,CancellationToken token)
        {var result=new TaskCompletionSource<EtfHolderCandidateReport>(TaskCreationOptions.RunContinuationsAsynchronously);Replies.Add(result);return result.Task;}
    }
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS ETF持有人界面："+label);}
}
