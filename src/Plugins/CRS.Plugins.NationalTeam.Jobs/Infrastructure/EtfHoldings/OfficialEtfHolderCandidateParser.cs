using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed class OfficialEtfHolderCandidateParser : IEtfHolderCandidateParser
{
    public EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token)=>Huatai300HolderPdf.Parse(captured,token);
}
