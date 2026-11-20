using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed record SouthboundCapturedPage(string Url,byte[] Content,DateTimeOffset RetrievedAt);
public sealed record SouthboundCapture(DateOnly From,DateOnly Through,IReadOnlyList<SouthboundCapturedPage> Pages);
public interface ISouthboundReadRepository
{
    SouthboundAnnualReport SouthboundAnnual(int firstYear,int lastYear);
    string? SouthboundReceipt(Guid requestId);
}
public interface ISouthboundWriteRepository
{
    string SaveSouthbound(SouthboundCapture capture,Guid? commandId);
}
