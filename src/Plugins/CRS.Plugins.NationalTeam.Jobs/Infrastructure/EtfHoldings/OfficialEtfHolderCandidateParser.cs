// 按已支持原件模板选择ETF持有人解析器；未知模板返回明确状态，候选解析不写入具名持仓审核。
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed class OfficialEtfHolderCandidateParser : IEtfHolderCandidateParser
{
    public EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token)=>captured.Document switch
    {
        {Market:"SZSE",Ticker:"159915"}=>EfundChiNextHolderPdf.Parse(captured,token),
        {Market:"SZSE",Ticker:"159919"}=>HarvestCsi300HolderPdf.Parse(captured,token),
        _=>Huatai300HolderPdf.Parse(captured,token)
    };
}
