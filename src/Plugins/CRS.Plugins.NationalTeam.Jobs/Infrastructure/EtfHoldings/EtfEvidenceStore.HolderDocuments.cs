using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal sealed partial class EtfEvidenceStore
{
    /// <summary>ETF持仓模块提供已登记原件窄端口；当前证券、来源记录及Q15在同一快照绑定，不写审核或通知。</summary>
    public EtfHolderDocument EtfHolderDocument(EtfKey key,string documentId)
    {
        if(documentId.Length!=64||!documentId.All(Uri.IsHexDigit))throw new InvalidDataException("ETF原件标识无效。");
        var document=RequireAlertDocument(documentId,false);
        if(document.Market!=key.Market||document.Ticker!=key.Ticker)throw new InvalidDataException("ETF原件证券不符。");
        return new(document,raw.ReadVerified(document.Hash,session.OperationCancellation));
    }
}
