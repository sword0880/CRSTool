namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>日常总览只取最新有效基准中的已核账户；基准关闭时不以健康旧期补位。</summary>
public static class NamedEtfHoldingOverview
{
    public static IReadOnlyList<NamedEtfHistoryRow> Latest(NamedEtfHoldingHistoryReport report)
    {
        if(report.Baseline.State!="LatestReviewedDisclosedSubset")return [];
        var accounts=report.Baseline.Holdings.Select(x=>(Legal:x.LegalEntityId.Trim(),Account:x.Account.Trim())).ToHashSet();
        return report.Rows.Where(x=>x.ReportAsOf==report.Baseline.ReportAsOf&&accounts.Contains((x.LegalEntityId.Trim(),x.Account.Trim())))
            .OrderBy(x=>x.InstitutionGroup,StringComparer.Ordinal).ThenBy(x=>x.LegalName,StringComparer.Ordinal).ThenBy(x=>x.Account,StringComparer.Ordinal).ToArray();
    }
}
