namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>已逐页核对的固定原文目录，条目存在不代表持仓已登记；官网列示日不是最早公开时刻。</summary>
public sealed record HistoricalEquityPdf(string Id,string Market,string Ticker,string Name,DateOnly ReportAsOf,DateOnly Published,
    string PublicationBasis,string DocumentUrl,string OriginalHash,string ExtractHash,string Pages,int HolderCount,int ReviewedAccounts,string Scope)
{
    public string DisplayName=>$"{Ticker} {Name} · {ReportAsOf:yyyy-MM-dd} · {ReviewedAccounts}个具名账户";
    public string ScopeText=>Scope=="Top10ShareholdersExcludingSecuritiesLending"?"前十名股东持股表，不含通过转融通出借股份；A股，总股本比例口径":"口径待核验";
}

/// <summary>与宿主持久命令分别回读的业务回执；Imported只证明事务提交，不代表机构交易或可比性批准。</summary>
public sealed record HistoricalEquityReceipt(Guid RequestId,string DefinitionId,string DisclosureId,string Outcome,int ReviewedAccounts,
    DateTimeOffset RecordedAt,string OriginalHash,string ExtractHash);
public sealed record HistoricalEquityCommand(Guid RequestId,string State,string Code,string? ExecutionId,bool ExistingRequest=false);
