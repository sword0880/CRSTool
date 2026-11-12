using CRS.AI.Contracts;

namespace CRS.AI.Query;

internal static class EtfCoverageValidation
{
    public static bool IsConsistent(AiCoverage coverage)
    {
        // 已登记交易日是分母。复核逐日状态，防止漏页或矛盾汇总被标记为完整。
        var days = coverage.Days;
        if (days.Count != coverage.RegisteredTradingDays) return false;
        var dates = new HashSet<DateOnly>();
        int accepted = 0, missing = 0, pending = 0, critical = 0, rejected = 0;
        foreach (var day in days)
        {
            if (day.Date < coverage.From || day.Date > coverage.To || !dates.Add(day.Date))
                return false;
            switch (day.State)
            {
                case "Accepted": accepted++; break;
                case "Missing": missing++; break;
                case "NeedsReview": pending++; break;
                case "Critical": critical++; break;
                case "CalendarCritical" when !coverage.CalendarVerified: critical++; break;
                case "QualityRejected": rejected++; break;
                default: return false;
            }
        }
        if (accepted != coverage.AcceptedDays || missing != coverage.MissingDays ||
            pending != coverage.PendingDays || critical != coverage.CriticalDays ||
            rejected != coverage.RejectedDays)
            return false;

        var percent = coverage.CalendarVerified && days.Count > 0
            ? decimal.Round(accepted * 100m / days.Count, 2) : (decimal?)null;
        var expectedState = days.Count == 0 ? "NoCalendar"
            : !coverage.CalendarVerified ? "CalendarCritical"
            : !coverage.CalendarRangeCovered ? "CalendarRangeIncomplete"
            : accepted == days.Count ? "CompleteRegisteredCalendar" : "Incomplete";
        return coverage.CoveragePercent == percent && coverage.State == expectedState;
    }
}
