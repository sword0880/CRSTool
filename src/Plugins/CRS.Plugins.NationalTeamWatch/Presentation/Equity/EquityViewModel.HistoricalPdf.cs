using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Presentation;

public partial class EquityViewModel
{
    public ObservableCollection<HistoricalEquityPdf> HistoricalDocuments {get;}=[];
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(ImportHistoricalPdfCommand))] private HistoricalEquityPdf? selectedHistoricalDocument;
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(ImportHistoricalPdfCommand))] private string historicalReviewer="";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(ImportHistoricalPdfCommand))] private string historicalReason="补入已核对的历史报告期持仓，性质及可比性待核验";
    [ObservableProperty,NotifyCanExecuteChangedFor(nameof(ImportHistoricalPdfCommand))] private bool historicalOriginalConfirmed;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(HistoricalCanEdit)),NotifyCanExecuteChangedFor(nameof(CheckHistoricalImportCommand))] private Guid? historicalRequestId;
    [ObservableProperty,NotifyPropertyChangedFor(nameof(HistoricalCanEdit))] private string historicalState="";
    [ObservableProperty] private string historicalStatus="先读取可补报告。目录不代表已登记；本次只补报告期持仓，不批准期间增减或机构交易。";
    public bool HistoricalCanEdit=>HistoricalRequestId is null||HistoricalState=="Completed";
    private bool CanImportHistoricalPdf()=>CanManage&&SelectedHistoricalDocument is not null&&HistoricalOriginalConfirmed
        &&!string.IsNullOrWhiteSpace(HistoricalReviewer)&&!string.IsNullOrWhiteSpace(HistoricalReason);
    private bool CanCheckHistoricalImport()=>HistoricalRequestId.HasValue;
    [RelayCommand] private Task LoadHistoricalDocuments()=>ExecuteAsync(async()=>
    {
        HistoricalStatus="正在读取可补历史原文目录…";
        try
        {
            var rows=await workspace.HistoricalEquityDocumentsAsync(lifetime);var selected=SelectedHistoricalDocument?.Id;
            HistoricalDocuments.Clear();foreach(var row in rows)HistoricalDocuments.Add(row);
            SelectedHistoricalDocument=rows.FirstOrDefault(x=>x.Id==selected)??rows.FirstOrDefault();
            HistoricalStatus=$"可补 {rows.Count} 份固定原文。请核对原文；未列示不等于零，登记时间保持实际知识时点。";
        }
        catch(NotSupportedException){HistoricalDocuments.Clear();SelectedHistoricalDocument=null;HistoricalStatus="当前后台尚不支持历史原文登记，请先更新后台。";}
        catch(Exception error){HistoricalDocuments.Clear();SelectedHistoricalDocument=null;HistoricalStatus="历史原文目录读取失败："+error.Message;}
    });
    [RelayCommand(CanExecute=nameof(CanImportHistoricalPdf))] private Task ImportHistoricalPdf()=>ExecuteAsync(async()=>
    {
        var document=SelectedHistoricalDocument??throw new InvalidDataException("请选择历史原文。");
        var command=await workspace.ImportHistoricalEquityPdfAsync(document.Id,HistoricalReviewer,HistoricalReason,HistoricalOriginalConfirmed,lifetime);
        HistoricalRequestId=command.RequestId;HistoricalState=command.State;HistoricalOriginalConfirmed=false;
        // 已受理/冷却只显示持久请求，不轮询或创建替代任务；用户主动回查同一ID。
        HistoricalStatus=(command.ExistingRequest?"后台已有历史登记，本次未另建请求。":"历史登记已受理。")+$" 请求 {command.RequestId} · {command.State}/{command.Code}。请查看原请求。";
        if(command.State=="Completed")await ReadHistoricalCompletion(command);
    });
    [RelayCommand(CanExecute=nameof(CanCheckHistoricalImport))] private Task CheckHistoricalImport()=>ExecuteAsync(async()=>
    {
        if(HistoricalRequestId is not {} id)return;var command=await workspace.HistoricalEquityStatusAsync(id,lifetime);
        HistoricalState=command.State;HistoricalStatus=$"原请求 {id} · {command.State}/{command.Code}。等待来源冷却不表示登记完成。";
        if(command.State=="Completed")await ReadHistoricalCompletion(command);
    });
    private async Task ReadHistoricalCompletion(HistoricalEquityCommand command)
    {
        if(command.Code!="Succeeded"){HistoricalStatus=$"原请求已结束：{command.Code}。未按历史持仓登记成功展示，请核对任务记录。";return;}
        var receipt=await workspace.HistoricalEquityReceiptAsync(command.RequestId,lifetime);
        if(receipt is null||receipt.RequestId!=command.RequestId)
        {HistoricalState="Unconfirmed";HistoricalStatus="宿主任务已结束，但业务回执尚未核实；保留原请求，不重复提交。";return;}
        HistoricalStatus=SelectedHistoricalDocument?.Id!=receipt.DefinitionId?"原请求登记的是其他历史原文；当前选择未另行提交。"
            :receipt.Outcome=="AlreadyRegistered"?"该历史原文已登记，本次没有新增持仓事实。"
            :$"历史原文已登记，核验 {receipt.ReviewedAccounts} 个具名账户。性质与期间可比性仍未知，不据此确认买卖。";
        await RefreshAsync();
    }
}
