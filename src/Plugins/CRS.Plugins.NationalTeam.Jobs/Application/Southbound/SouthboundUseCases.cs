using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed class SouthboundUseCases:MarketUseCaseScope
{
    internal SouthboundUseCases(MarketApplicationSession session):base(session){}
    public Task<SouthboundAnnualReport> AnnualAsync(int first,int last,CancellationToken token)=>ReadPortAsync<ISouthboundReadRepository,SouthboundAnnualReport>(r=>r.SouthboundAnnual(first,last),token);
    public Task<string?> ReceiptAsync(Guid request,CancellationToken token)=>ReadPortAsync<ISouthboundReadRepository,string?>(r=>r.SouthboundReceipt(request),token);
    public Task<string> SaveAsync(SouthboundCapture capture,Guid? command,CancellationToken token)=>RunAsync<ISouthboundWriteRepository,string>(r=>r.SaveSouthbound(capture,command),token);
}
