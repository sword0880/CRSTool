using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

/// <summary>人工原件核验草稿；首见时间、哈希与确认结果由后台保存，界面不能自行升级证据。</summary>
public partial class ExitAlertViewModel
{
    public ObservableCollection<EtfConfiguration> ProofFunds {get;}=[];
    public ObservableCollection<AlertDocument> ProofDocuments {get;}=[];
    public ObservableCollection<NamedEtfHolding> ProofHoldings {get;}=[];
    [ObservableProperty] private EtfConfiguration? proofFund;
    [ObservableProperty] private AlertDocument? proofDocument;
    [ObservableProperty] private string proofUrl="";
    [ObservableProperty] private string fundId="";
    [ObservableProperty] private string managerId="";
    [ObservableProperty] private string indexId="";
    [ObservableProperty] private DateTime? comparableFrom=Today.AddYears(-1);
    [ObservableProperty] private DateTime? comparableThrough=Today;
    [ObservableProperty] private string proofReviewer="";
    [ObservableProperty] private bool metadataConfirmed;
    [ObservableProperty] private string legalEntityId="";
    [ObservableProperty] private string legalName="";
    [ObservableProperty] private string holdingAccount="";
    [ObservableProperty] private DateTime? holdingAsOf;
    [ObservableProperty] private string publishedAt="";
    [ObservableProperty] private decimal holdingShares;
    [ObservableProperty] private string evidencePage="";
    [ObservableProperty] private bool holdingConfirmed;
    [ObservableProperty] private bool independentOwnershipConfirmed;
    private int proofVersion;
    public Task ProofLoadTask {get;private set;}=Task.CompletedTask;
    partial void OnProofFundChanged(EtfConfiguration? value)=>ProofLoadTask=LoadProofDataAsync(++proofVersion);
    partial void OnProofDocumentChanged(AlertDocument? value){MetadataConfirmed=false;HoldingConfirmed=false;IndependentOwnershipConfirmed=false;ClearHolderCandidates();}
    private async Task LoadProofFundsAsync()
    {
        var previous=ProofFund;var funds=await etfWorkspace.ConfigurationAsync(lifetime);
        ProofFunds.Clear();foreach(var fund in funds)ProofFunds.Add(fund);
        ProofFund=ProofFunds.FirstOrDefault(x=>x.Market==previous?.Market&&x.Ticker==previous?.Ticker)??ProofFunds.FirstOrDefault();
        await ProofLoadTask;
    }
    private async Task LoadProofDataAsync(int version)
    {
        ProofDocuments.Clear();ProofHoldings.Clear();ProofDocument=null;FundId="";ManagerId="";IndexId="";
        MetadataConfirmed=false;HoldingConfirmed=false;IndependentOwnershipConfirmed=false;
        if(ProofFund is not {} fund)return;
        try
        {
            var key=new EtfKey(fund.Market,fund.Ticker);var documents=await workspace.AlertDocumentsAsync(key,lifetime);
            var metadata=(await workspace.AlertMetadataAsync(lifetime)).SingleOrDefault(x=>x.Market==key.Market&&x.Ticker==key.Ticker);
            var holdings=await workspace.NamedEtfHoldingsAsync(key,lifetime);
            if(version!=proofVersion||lifetime.IsCancellationRequested)return;
            foreach(var document in documents)ProofDocuments.Add(document);
            foreach(var holding in holdings)ProofHoldings.Add(holding);
            ProofDocument=ProofDocuments.FirstOrDefault();
            if(metadata is not null){FundId=metadata.FundId;ManagerId=metadata.ManagerId;IndexId=metadata.IndexId;ComparableFrom=metadata.ComparableFrom.ToDateTime(TimeOnly.MinValue);ComparableThrough=metadata.ComparableThrough.ToDateTime(TimeOnly.MinValue);}
        }
        catch(OperationCanceledException){}catch(Exception error){if(version==proofVersion)Status="原件列表未读取："+error.GetType().Name;}
    }
    [RelayCommand] private Task RegisterDocument()=>Run(async()=>
    {
        if(!CanManage||ProofFund is not {} fund)throw new InvalidDataException("请先选择ETF。");
        await workspace.RegisterAlertDocumentAsync(new(fund.Market,fund.Ticker,ProofUrl),lifetime);await LoadProofDataAsync(++proofVersion);
        Status="原件已登记。请打开官方PDF逐项核对；登记不等于已确认可比性或具名持仓。";
    });
    [RelayCommand] private Task SaveMetadata()=>Run(async()=>
    {
        if(!CanManage||ProofDocument is null||ComparableFrom is null||ComparableThrough is null)throw new InvalidDataException("请先选择原件并填写可比区间。");
        await workspace.ReviewAlertMetadataAsync(new(ProofDocument.Id,FundId,ManagerId,IndexId,DateOnly.FromDateTime(ComparableFrom.Value),DateOnly.FromDateTime(ComparableThrough.Value),ProofReviewer,MetadataConfirmed),lifetime);
        MetadataConfirmed=false;await LoadEventsAsync();Status="可比口径审核已保存，仅在明确核验的日期范围内生效。";
    });
    [RelayCommand] private Task SaveNamedHolding()=>Run(async()=>
    {
        // 公告时间要求显式时区；没有准确时间不能把日期午夜填成已证实的公布时点。
        if(!CanManage||ProofDocument is null||HoldingAsOf is null||!(PublishedAt.EndsWith('Z')||System.Text.RegularExpressions.Regex.IsMatch(PublishedAt,@"[+-]\d{2}:\d{2}$"))
            ||!DateTimeOffset.TryParse(PublishedAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out var published))throw new InvalidDataException("请填写报告期及带时区的已核对公布时间，例如2026-08-28T18:00:00+08:00；时间未知时不能确认。");
        await workspace.ReviewNamedEtfHoldingAsync(new(ProofDocument.Id,LegalEntityId,LegalName,HoldingAccount,DateOnly.FromDateTime(HoldingAsOf.Value),published,HoldingShares,EvidencePage,ProofReviewer,HoldingConfirmed,IndependentOwnershipConfirmed),lifetime);
        await LoadProofDataAsync(++proofVersion);await LoadEventsAsync();Status="具名持仓已保存，首见时点由后台原件登记记录固定；不将匿名机构按份额匹配身份。";
    });
}
