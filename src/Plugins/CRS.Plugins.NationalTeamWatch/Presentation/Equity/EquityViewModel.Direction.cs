// 直接持股方向的界面模型，报告期、分类标准和原文价格由用户显式选择；草稿修改后重新要求原件确认。
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EquityViewModel
{
    public ObservableCollection<EquityDirectionGroup> DirectionGroups {get;}=[];
    public ObservableCollection<EquityDirectionPosition> DirectionPositions {get;}=[];
    public ObservableCollection<EquityDirectionGap> DirectionGaps {get;}=[];
    [ObservableProperty] private DateTime? directionDate;
    [ObservableProperty] private string directionStatus="按报告期读取当前启用股票池的已核具名A股样本；不沿用顶部搜索、机构或关注筛选。";
    [ObservableProperty] private string directionDenominator="行业及期末市值均需原文核验；缺失不补零。";
    [ObservableProperty] private EquityDirectionPosition? selectedDirectionPosition;
    [ObservableProperty] private string directionScheme="";
    [ObservableProperty] private string directionSchemeVersion="";
    [ObservableProperty] private string directionSectorCode="";
    [ObservableProperty] private string directionSectorName="";
    [ObservableProperty] private string directionStyle="";
    [ObservableProperty] private string directionPage="";
    [ObservableProperty] private string directionPrice="";
    [ObservableProperty] private string directionPricePage="";
    [ObservableProperty] private string directionReviewer="";
    [ObservableProperty] private string directionReason="";
    [ObservableProperty] private bool directionOriginalConfirmed;
    private int directionVersion;
    public bool DirectionCanEdit=>CanManage&&!Busy;
    public bool DirectionIsIdle=>!Busy;
    partial void OnBusyChanged(bool value){OnPropertyChanged(nameof(DirectionCanEdit));OnPropertyChanged(nameof(DirectionIsIdle));}
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.PropertyName is nameof(DirectionScheme) or nameof(DirectionSchemeVersion) or nameof(DirectionSectorCode) or nameof(DirectionSectorName)
            or nameof(DirectionStyle) or nameof(DirectionPage) or nameof(DirectionPrice) or nameof(DirectionPricePage) or nameof(DirectionReviewer) or nameof(DirectionReason))
            DirectionOriginalConfirmed=false;
    }
    partial void OnDirectionDateChanged(DateTime? value)
    {directionVersion++;ClearDirection();DirectionStatus="报告期已改变，请重新读取。留空选择当前已核披露的最近报告期。";}
    private void ClearDirection()
    {DirectionGroups.Clear();DirectionPositions.Clear();DirectionGaps.Clear();SelectedDirectionPosition=null;DirectionDenominator="行业及期末市值均需原文核验；缺失不补零。";}
    partial void OnSelectedDirectionPositionChanged(EquityDirectionPosition? value)
    {
        DirectionOriginalConfirmed=false;var fact=value?.Fact;
        DirectionScheme=fact?.Scheme??"";DirectionSchemeVersion=fact?.SchemeVersion??"";DirectionSectorCode=fact?.SectorCode??"";DirectionSectorName=fact?.SectorName??"";
        DirectionStyle=fact?.Style??"";DirectionPage=fact?.Page??"";DirectionPrice=fact?.ClosingPriceCny?.ToString(CultureInfo.InvariantCulture)??"";DirectionPricePage=fact?.PricePage??"";
        DirectionReason="";
    }
    public async Task LoadDirectionAsync()
    {
        var version=++directionVersion;var period=DirectionDate is {} value?DateOnly.FromDateTime(value):(DateOnly?)null;
        ClearDirection();DirectionStatus="正在读取同报告期方向报告…";
        try
        {
            var report=await workspace.EquityDirectionAsync(period,lifetime);
            if(version!=directionVersion||lifetime.IsCancellationRequested)return;
            foreach(var row in report.Groups)DirectionGroups.Add(row);foreach(var row in report.Positions)DirectionPositions.Add(row);foreach(var gap in report.Gaps)DirectionGaps.Add(gap);
            DirectionDenominator=report.Denominator;
            DirectionStatus=$"报告期 {report.ReportAsOf?.ToString("yyyy-MM-dd")??"尚无已核披露"} · 启用股票 {report.ActiveSecurities} · 已核快照 {report.DisclosedSecurities} 只/{report.ReviewedAccounts} 个账户 · 行业已核 {report.ClassifiedSecurities} 只 · 期末价已核 {report.PricedSecurities} 只。仅为已披露样本，不代表完整国家队组合；查询时刻 {CRS.Timing.BeijingTime.Format(report.ObservedAt)}。";
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(version==directionVersion){ClearDirection();DirectionStatus="方向报告不可用："+(error is InvalidDataException?error.Message:error.GetType().Name);}}
    }
    [RelayCommand] private Task ReadDirection()=>ExecuteAsync(LoadDirectionAsync);
    [RelayCommand] private Task ReviewDirection()=>ExecuteAsync(async()=>
    {
        if(!CanManage||SelectedDirectionPosition is not {} position||!DirectionOriginalConfirmed)throw new InvalidDataException("请选择已核持仓并明确确认原文中的行业及价格依据。");
        decimal? price=null;
        if(!string.IsNullOrWhiteSpace(DirectionPrice))
        {if(!decimal.TryParse(DirectionPrice,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var parsed)||parsed<=0)throw new InvalidDataException("期末价请输入正数；没有同原文依据请留空。");price=parsed;}
        var review=new EquityDirectionReview(position.Holding.DisclosureId,position.Holding.EvidenceHash??"",DirectionScheme.Trim(),DirectionSchemeVersion.Trim(),DirectionSectorCode.Trim(),DirectionSectorName.Trim(),
            string.IsNullOrWhiteSpace(DirectionStyle)?null:DirectionStyle.Trim(),DirectionPage.Trim(),DirectionReviewer.Trim(),DirectionReason.Trim(),true,
            price,price is null?null:position.Holding.ReportAsOf,price is null?null:DirectionPricePage.Trim(),position.Fact?.ReviewId);
        await workspace.ReviewEquityDirectionAsync(review,lifetime);DirectionOriginalConfirmed=false;
        await LoadDirectionAsync();
    });
}
