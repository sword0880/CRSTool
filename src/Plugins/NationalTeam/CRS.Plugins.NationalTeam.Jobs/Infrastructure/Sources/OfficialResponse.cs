using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>保留失败响应与解析器版本；状态通过结构化数据传递，不解析异常消息。</summary>
internal static class OfficialResponse
{
    internal const string FailureKey="EtfSourceFailure";
    internal static InvalidDataException InvalidPage(string market,string? ticker,SourcePage page,string parser,IRawEvidenceStore? raw,string reason)
    {
        var error=new InvalidDataException(reason);
        error.Data[FailureKey]=new SourceFailure(market,ticker,"ParseFailed","PaginationContractFailed",page.Uri.AbsoluteUri,raw?.Save(page.Content),parser);
        return error;
    }
    internal static T Parse<T>(string market,string? ticker,Uri uri,byte[] bytes,int status,string parser,IRawEvidenceStore? raw,Func<T> parse)
    {
        var hash=raw?.Save(bytes);
        if(status!=200)
        {
            var error=new InvalidDataException($"官方份额请求HTTP {status}。");
            error.Data[FailureKey]=new SourceFailure(market,ticker,status is 401 or 403 or 429?"AccessBlocked":"SourceFailed","Http"+status,uri.AbsoluteUri,hash,parser);
            throw error;
        }
        try{return parse();}
        catch(Exception error) when(error is not OperationCanceledException)
        {
            error.Data[FailureKey]=new SourceFailure(market,ticker,"ParseFailed","ResponseContractFailed",uri.AbsoluteUri,hash,parser);
            throw;
        }
    }
}
