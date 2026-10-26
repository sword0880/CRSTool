using System.IO;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;
internal sealed class SourceFetchStore(IMarketRepositorySession session)
{
    private Action? beforeWrite=>session.BeforeWrite;
    private RawEvidenceStore raw=>session.RawEvidence;
    private string Now=>session.Now;
    private void Execute(string sql,params (string,object?)[] args)=>session.Execute(sql,args);
    public string RecordFetch(string hash, string sourceUrl, string? originalHash = null,string provider="ReviewedNormalizedManualImport",string parserVersion="normalized-csv-v1")
    {
        beforeWrite?.Invoke();
        if (!Uri.TryCreate(sourceUrl,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length!=0 || !uri.IsDefaultPort
            || !(uri.Host=="sse.com.cn" || uri.Host.EndsWith(".sse.com.cn",StringComparison.Ordinal) || uri.Host=="szse.cn" || uri.Host.EndsWith(".szse.cn",StringComparison.Ordinal)
                ||provider=="ReviewedEquityDisclosurePdf"&&OfficialEquityDocuments.IsReviewedIssuer(uri)))
            throw new InvalidDataException("仅接受已登记的官方HTTPS来源；发行人原文须匹配固定证券、报告期及已核验哈希。");
        if (!raw.Verify(hash)) throw new InvalidDataException("Evidence is missing or damaged.");
        originalHash ??= hash;
        if (!raw.Verify(originalHash)) throw new InvalidDataException("Original official file evidence is missing or damaged.");
        var id = Guid.NewGuid().ToString("N");
        // 自动响应与人工标准化来源分别登记，历史原件和解析器版本可以追溯。
        Execute("INSERT INTO source_fetch VALUES($id,$provider,$now,NULL,$url,$hash,$original,'RawSaved',$parser)",("$id",id),("$provider",provider),("$parser",parserVersion),("$now",Now),("$url",sourceUrl),("$hash",hash),("$original",originalHash));
        return id;
    }
    public void MarkFailed(string fetchId) {beforeWrite?.Invoke();Execute("UPDATE source_fetch SET status='ParseFailed' WHERE fetch_id=$id",("$id",fetchId));}
}
