using System.Text.Json;
using CRS.Common.Integrity;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>独立ETF原件采集的版本化入口；与人工审核分开，市场间不共享持久退避。</summary>
public static class EtfDocumentCollectionProtocol
{
    public const string Capability="etf-document-collection-v1";
    public const string Operation="etf-document-register-v1";
    public const string SseJob="etf-document-sse";
    public const string SzseJob="etf-document-szse";
    public static string RequestFingerprint(AlertDocumentRequest request)=>ContentFingerprint.Sha256(JsonSerializer.SerializeToUtf8Bytes(request));
    public static string Job(string market)=>market switch
    {"SSE"=>SseJob,"SZSE"=>SzseJob,_=>throw new InvalidDataException("ETF原件市场不受支持。")};
    public static bool MatchesSource(string market,Uri uri)=>market switch
    {
        "SSE"=>uri.Host is "www.sse.com.cn" or "query.sse.com.cn" or "etf.sse.com.cn" or "static.sse.com.cn",
        "SZSE"=>uri.Host is "www.szse.cn" or "disc.static.szse.cn",
        _=>false
    };
}
