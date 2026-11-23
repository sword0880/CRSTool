namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>基金公告目录线索，不是经核实持仓；公告日期/原文时间保留，不能推造准确发布时间。</summary>
public sealed record EtfReportIndexDocument(string ReferenceId,string Title,string Kind,DateOnly Announced,string OriginalPublication,
    string DocumentUrl,IReadOnlyList<string> SecurityCodes);

/// <summary>一页官方目录及其声明总数；来源没有提供冻结快照，不能据此声称历史披露覆盖完整。</summary>
public sealed record EtfReportIndexPage(int Total,int Pages,IReadOnlyList<EtfReportIndexDocument> Documents);

public sealed record EtfReportIndexSnapshot(EtfKey Key,string State,string? SearchId,DateOnly? From,DateOnly? Through,
    string? CapturedAt,int? DeclaredTotal,IReadOnlyList<EtfReportIndexDocument> Documents,string FormatVersion)
{
    public const string Version="etf-report-index-v1";
    public bool HoldingsConfirmed=>false;
    public string CoverageProof=>"UnversionedDirectory";
}
public sealed record EtfReportSearchReceipt(Guid RequestId,string State,string Code);
