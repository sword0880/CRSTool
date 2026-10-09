namespace CRS.Domain;

/// <summary>集中判定运行的四维状态，目录重整阶段沿用现有准入规则。</summary>
public static class ResultStatusPolicy
{
    /// <summary>根据已有问题、对账和汇率信息计算状态；税务证据规则另按整改工单完善。</summary>
    public static (CalculationStatus calculationStatus, DataCompleteness completeness,
        ReconciliationStatus reconciliation, UsageLabel usage, decimal? estimated) Evaluate(
        List<Issue> finalIssues, bool scopeConfirmed, bool positionReconciled,
        List<CashReconciliation> cashChecks, List<PnlReconciliation> pnlChecks,
        bool provisional, IEnumerable<AppliedRate> appliedRates, TaxSummary summary, bool hasIndependentPnlEvidence = true)
    {
        var completenessCodes = new[] { "INCOMPLETE_PERIOD", "OPENING_UNCONFIRMED", "SCOPE_UNCONFIRMED", "MISSING_END_POSITIONS",
            "MISSING_COST", "POSITION_MISMATCH", "UNSUPPORTED_TRANSFER", "UNSUPPORTED_SECTION", "CASH_REPORT_MISMATCH",
            "MISSING_TRADE_FILE", "MISSING_CASH_FLOWS", "MISSING_ACCOUNT_INCOME", "INCOME_ACCOUNT_UNCONFIRMED", "OPENING_CONFLICT" };
        var incomplete = finalIssues.Any(i => completenessCodes.Contains(i.Code)) || !scopeConfirmed;
        var hardBlocked = finalIssues.Any(i => i.Code is "DUPLICATE_CONFLICT" or "LOT_OVERLAP" or "XML_INVALID");
        var reconciliation = cashChecks.Count == 0 && pnlChecks.Count == 0 && !positionReconciled ? ReconciliationStatus.NotVerifiable
            : cashChecks.Any(c => c.Status != "一致") || pnlChecks.Any(p => p.Status != "一致") || !positionReconciled
                ? ReconciliationStatus.Differences : cashChecks.Count == 0 || !hasIndependentPnlEvidence ? ReconciliationStatus.NotVerifiable : ReconciliationStatus.Matched;
        var calculationStatus = hardBlocked ? CalculationStatus.Blocked : finalIssues.Count == 0 ? CalculationStatus.Completed : CalculationStatus.Partial;
        var completeness = incomplete ? DataCompleteness.Incomplete : finalIssues.Count == 0 ? DataCompleteness.Confirmed : DataCompleteness.Unconfirmed;
        var usage = provisional ? UsageLabel.Provisional : calculationStatus == CalculationStatus.Completed && reconciliation == ReconciliationStatus.Matched
            ? UsageLabel.ReviewReady : UsageLabel.ReviewOnly;
        var estimated = calculationStatus == CalculationStatus.Completed && completeness == DataCompleteness.Confirmed
            && reconciliation == ReconciliationStatus.Matched && !provisional && appliedRates.All(r => r.SourceConfirmed) ? (decimal?)summary.SupplementTax : null;
        return (calculationStatus, completeness, reconciliation, usage, estimated);
    }
}
