using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

public sealed partial class ExitAlertUseCases
{
    /// <summary>先结束独立数据库读取，再解析冻结PDF；不占用写会话或自动保存金融审核。</summary>
    public async Task<EtfHolderCandidateReport> EtfHolderCandidatesAsync(EtfKey key,string documentId,CancellationToken token)
    {
        var document=await ReadPortAsync((IExitAlertReadRepository current)=>current.EtfHolderDocument(key,documentId),token);
        return await Task.Run(()=>holderParser.Parse(document,token),token);
    }
}
