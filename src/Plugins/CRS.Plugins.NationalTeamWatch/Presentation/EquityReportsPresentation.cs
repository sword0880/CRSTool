using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EquityViewModel
{
    public ObservableCollection<EquityReportDocument> Reports {get;}=[];
    [ObservableProperty] private EquityReportDocument? selectedReport;
    [ObservableProperty] private string reportStatus="选择股票后，可由后台查找最近400天的官方定期报告。";
    private async Task LoadReportDocumentsAsync(EquityHoldingRow? row,int version)
    {
        Reports.Clear();SelectedReport=null;
        if(row is null){ReportStatus="选择股票后查找官方报告。";return;}
        try
        {
            var reports=await workspace.EquityReportsAsync(new(row.Market,row.Ticker),lifetime);
            if(version!=detailVersion||lifetime.IsCancellationRequested)return;
            foreach(var report in reports)Reports.Add(report);
            ReportStatus=reports.Count==0?"尚无已缓存报告目录。可查找官方报告；空目录不代表没有持仓。":$"已缓存 {reports.Count} 份完整报告线索；仅目录信息，PDF股东表及报告期仍须核对。";
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(version==detailVersion)ReportStatus="官方报告目录暂不可用："+error.GetType().Name;}
    }
    [RelayCommand] private Task RefreshReports()=>ExecuteAsync(()=>LoadReportDocumentsAsync(Selected,detailVersion));
    [RelayCommand] private Task FindReports()=>ExecuteAsync(async()=>
    {
        var row=Selected;if(row is null)return;
        var receipt=await workspace.QueueEquityReportsAsync(new(row.Market,row.Ticker),lifetime);
        if(Selected?.Market!=row.Market||Selected?.Ticker!=row.Ticker)return;
        if(receipt.Code=="Rejected"){ReportStatus="官方报告检索未执行，请查看任务记录。当前只支持已核验的沪市查询。";return;}
        ReportStatus=$"官方报告检索已受理：{receipt.State} / {receipt.Code}。后台执行完成后刷新目录；来源限流仍会等待。任务 {receipt.RequestId}";
    });
    [RelayCommand] private void UseReport()
    {
        if(SelectedReport is not {} report||Selected is null||SelectedDisclosure is null||report.Market!=Selected.Market||report.Ticker!=Selected.Ticker)return;
        if(report.State!="Indexed"||SelectedDisclosure.State=="Critical"){ReportStatus="原始证据异常，不能填入核验资料。请恢复证据或重新检索。";return;}
        if(SelectedDisclosure.State=="Accepted"&&SelectedDisclosure.DocumentUrl!=report.DocumentUrl){ReportStatus="该披露已核验，原文不能替换。更正报告应选择新候选并逐账户重新核验。";return;}
        DocumentUrl=report.DocumentUrl;Published=report.Announced.ToDateTime(TimeOnly.MinValue);
        OriginalConfirmed=false;Comparable=false;
        if(SelectedDisclosure?.State!="Accepted")ReportAsOf=null;
        ReportStatus="已填写官方原文链接及目录公告日。请打开PDF核对股东表、报告截止日、单位及股类，再逐账户确认；没有自动批准持仓。";
    }
}
