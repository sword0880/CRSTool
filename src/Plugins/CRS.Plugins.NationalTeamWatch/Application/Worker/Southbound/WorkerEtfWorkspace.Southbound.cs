using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Serialization;
using CRS.Worker.Ipc;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace:ISouthboundWorkspace
{
    public async Task<SouthboundAnnualReport> SouthboundAnnualAsync(int firstYear,int lastYear,CancellationToken token)
    {
        SouthboundAnnualComparison.ValidateYears(firstYear,lastYear,CRS.Timing.BeijingClock.Now.Year);
        var blocks=(await ReadPagesAsync<JsonDocumentBlock>(new("southbound-annual",From:new(firstYear,1,1),To:new(lastYear,12,31),Count:2),token)).Rows;
        var report=JsonDocumentBlocks.Decode<SouthboundAnnualReport>(blocks,token);
        if(report.FirstYear!=firstYear||report.LastYear!=lastYear||report.Years.Count!=lastYear-firstYear+1
            ||!report.Years.Select(x=>x.Year).SequenceEqual(Enumerable.Range(firstYear,lastYear-firstYear+1))
            ||report.Through>DateOnly.FromDateTime(CRS.Timing.BeijingClock.Now.DateTime)
            ||report.Years.Any(y=>y.Points.Any(p=>p.Date.Year!=y.Year||p.Date>report.Through||p.CalendarDay!=new DateOnly(2000,p.Date.Month,p.Date.Day).DayOfYear-1||p.BuyYiHkd<0)
                ||y.Points.Select(p=>p.Date).Distinct().Count()!=y.Points.Count))throw new InvalidDataException("南向年度报告范围、坐标或金额无效。");
        return report;
    }
    private BackgroundRequest? southboundRequest;
    public async Task<SouthboundCommandReceipt> QueueSouthboundAsync(CancellationToken token)
    {
        // 未知受理结果保留原身份，不因等待超时重派采集。
        var request=southboundRequest??=new(WorkerProtocol.Version,Guid.NewGuid(),"RunNow",Plugin,"southbound-sync",Payload:EtfBusinessProtocol.WriteRequest(new("southbound-sync")));
        var reply=await SendAsync(request,token);
        var receipt=reply.Receipt??throw new InvalidDataException("南向更新受理尚未确认，请保持原请求核查。");
        if(receipt.RequestId!=request.RequestId)throw new InvalidDataException("南向更新回执身份不一致。");
        if(receipt.State=="Completed")southboundRequest=null;
        return new(request.RequestId,receipt.State,receipt.Code);
    }
    public async Task<SouthboundCommandReceipt> SouthboundStatusAsync(Guid requestId,CancellationToken token)
    {
        var reply=await SendAsync(new(WorkerProtocol.Version,Guid.NewGuid(),"GetCommand",RelatedRequestId:requestId),token);
        if(reply.Code!="Ok"||reply.Receipt is not {} receipt||receipt.RequestId!=requestId)throw new InvalidDataException("南向更新结果尚未确认，请沿原请求继续核查。");
        if(receipt.State=="Completed"&&southboundRequest?.RequestId==requestId)southboundRequest=null;
        return new(requestId,receipt.State,receipt.Code);
    }
}
