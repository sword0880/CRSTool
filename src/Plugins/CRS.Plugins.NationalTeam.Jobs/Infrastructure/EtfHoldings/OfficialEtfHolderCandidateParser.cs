using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed class OfficialEtfHolderCandidateParser : IEtfHolderCandidateParser
{
    public EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token)=>
        captured.Document is{Market:"SZSE",Ticker:"159915"}?EfundChiNextHolderPdf.Parse(captured,token):Huatai300HolderPdf.Parse(captured,token);
}
