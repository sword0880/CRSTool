using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch;

public sealed partial class WorkerEtfWorkspace:IAutomaticEquityWorkspace
{
    public async Task<AutomaticEquityStatus> AutomaticEquityStatusAsync(CancellationToken token)
    {
        var rows=(await ReadPagesAsync<AutomaticEquityStatus>(new("equity-auto-status"),token)).Rows;
        return rows.Count==1?rows[0]:throw new InvalidDataException("自动持股状态不完整，不能当作没有新报告。");
    }
}
