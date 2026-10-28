using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Application;

/// <summary>受限业务参数不携带本机路径、类型名或任意 SQL；写操作随 RunNow 持久化。</summary>
public sealed record EtfRequest(string Operation,string? Market=null,string? Ticker=null,DateOnly? From=null,
    DateOnly? To=null,string? Revision=null,bool Accept=false,string? Reviewer=null,bool Comparable=false,
    string? Name=null,string? Index=null,bool Active=false,string? Reason=null,int Offset=0,int Count=10,bool Original=false,string? Snapshot=null,long? ConfigurationRevision=null,
    string? Search=null,string? Institution=null,string? Nature=null,bool Favorites=false,EquityWatch? EquityWatch=null,EquityReview? EquityReview=null,
    ExitAlertFilter? AlertFilter=null,ExitAlertConfiguration? AlertConfiguration=null,AlertDocumentRequest? AlertDocument=null,
    AlertMetadataReview? AlertMetadata=null,NamedEtfHoldingReview? NamedEtfHolding=null,int ContractVersion=EtfBusinessProtocol.Version,bool RefreshSource=false,
    EquityDirectionReview? EquityDirectionReview=null,DateTimeOffset? KnowledgeCutoff=null,
    MarketBreadthSubmission? BreadthSubmission=null,MarketBreadthReview? BreadthReview=null)
{
    public EtfRequest? ResearchRequest {get;init;}
}
/// <summary>分页使用服务端短期快照，避免采集同时写入导致桌面分页重复或遗漏。</summary>
public sealed record EtfQueryPage(string Items,string Snapshot,int? NextOffset,string? Metadata=null,int ContractVersion=EtfBusinessProtocol.Version)
{
    // V1可选能力，不改原构造签名；缺失表示旧服务仍按十行读取，页字节限额不随行数增加。
    public int? MaximumPageCount {get;init;}
    // 同一快照的完整行数；新端据此识别提前结束，旧服务缺失时沿既有游标契约读取。
    public int? TotalRows {get;init;}
}
/// <summary>证据导出按有界字节块返回，桌面自行选择输出位置，服务不接收用户路径。</summary>
public sealed record EvidenceChunk(string Hash,long Length,int Offset,byte[] Bytes,int ContractVersion=EtfBusinessProtocol.Version);
