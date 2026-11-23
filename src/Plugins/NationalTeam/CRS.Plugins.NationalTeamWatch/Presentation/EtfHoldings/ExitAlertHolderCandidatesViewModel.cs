// ETF原件候选的界面状态管理，切换证券或原件时取消旧等待，解析候选与人工持仓确认保持分离。
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class ExitAlertViewModel
{
    public ObservableCollection<EtfHolderCandidate> HolderCandidates {get;}=[];
    [ObservableProperty] private EtfHolderCandidate? holderCandidate;
    [ObservableProperty] private string holderCandidateStatus="选择已登记原件后可读取待核验候选。";
    private EtfHolderCandidateReport? holderReport;
    private CancellationTokenSource? holderCancellation;
    private int holderVersion;
    private void ClearHolderCandidates()
    {
        holderVersion++;holderCancellation?.Cancel();HolderCandidates.Clear();HolderCandidate=null;holderReport=null;
        HolderCandidateStatus="选择已登记原件后可读取待核验候选。";
        LegalEntityId="";LegalName="";HoldingAccount="";HoldingAsOf=null;PublishedAt="";HoldingPublicationDate=null;OnlyPublicationDate=false;HoldingShares=0;EvidencePage="";
        HoldingConfirmed=false;IndependentOwnershipConfirmed=false;
    }
    [RelayCommand] private async Task ReadHolderCandidates()
    {
        if(ProofDocument is not {} document||ProofFund is not {} fund)return;
        ClearHolderCandidates();var version=holderVersion;
        using var pending=CancellationTokenSource.CreateLinkedTokenSource(lifetime);holderCancellation=pending;
        HolderCandidateStatus="正在核验所选原件并读取持有人表…";
        try
        {
            var result=await etfEvidence.EtfHolderCandidatesAsync(new(fund.Market,fund.Ticker),document.Id,pending.Token);
            if(version!=holderVersion||pending.IsCancellationRequested)return;
            if(result.Document.Id!=document.Id||result.Document.Hash!=document.Hash||result.Document.Market!=fund.Market||result.Document.Ticker!=fund.Ticker)
                throw new InvalidDataException("候选结果与所选原件不一致。");
            holderReport=result;
            foreach(var row in result.Holders)HolderCandidates.Add(row);
            HolderCandidateStatus=result.State=="NeedsReview"
                ?$"{result.Explanation} 报告期：{result.ReportAsOf:yyyy-MM-dd}；公告日期：{result.PublicationDate:yyyy-MM-dd}；准确时刻未知。"
                :result.Explanation;
        }
        catch(OperationCanceledException){if(version==holderVersion)HolderCandidateStatus="已取消读取。";}
        catch(Exception error){if(version==holderVersion){HolderCandidates.Clear();holderReport=null;HolderCandidateStatus="候选读取不可用："+error.GetType().Name;}}
        finally{if(ReferenceEquals(holderCancellation,pending))holderCancellation=null;}
    }
    [RelayCommand] private void UseHolderCandidate()
    {
        if(HolderCandidate is not {} candidate||holderReport is not {State:"NeedsReview"} report||ProofDocument?.Id!=report.Document.Id)return;
        LegalEntityId="";LegalName=candidate.RegisteredHolder;HoldingAccount=candidate.RegisteredHolder;
        HoldingAsOf=report.ReportAsOf?.ToDateTime(TimeOnly.MinValue);HoldingShares=candidate.Shares;EvidencePage=candidate.EvidencePage;
        PublishedAt="";HoldingPublicationDate=report.PublicationDate?.ToDateTime(TimeOnly.MinValue);OnlyPublicationDate=report.PublicationDate is not null;
        HoldingConfirmed=false;IndependentOwnershipConfirmed=false;
        HolderCandidateStatus="候选已填入核验草稿，仅带入报告中的公告日期，准确时刻保持未知。法人标识和独立持有关系仍需补证；保存前请逐项核对原件。";
    }
}
