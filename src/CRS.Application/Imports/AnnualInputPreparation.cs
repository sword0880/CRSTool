using CRS.Application.Abstractions;

namespace CRS.Application;

/// <summary>检查年度报告范围及期初成本来源，供原始导入和规范化重放共用。</summary>
internal sealed class AnnualInputPreparation(ICarryValidator carryValidator)
{
    public PreparedAnnualInput Prepare(ImportData data, int year, bool openingZero,
        bool scopeConfirmed, CarryDocument? carry, CancellationToken cancellation)
    {
        var begin = new DateOnly(year, 1, 1); var end = new DateOnly(year, 12, 31);
        var accounts = data.Sources.Where(s => !s.Opening && s.Start <= end && s.End >= begin).Select(s => s.Account).Distinct().Order().ToArray();
        if (accounts.Length == 0) throw new CrsException("导入报告不包含所选年度。");
        if (carry is not null)
        {
            carryValidator.Validate(carry, year, accounts);
            if (carry.Lots.Any(l => l.Key.Broker != data.Broker)) throw new CrsException("结转证券批次的券商与本次导入不一致。");
            data.OpeningLots.AddRange(carry.Lots.Select(l => l.Copy())); data.OpeningRounding.AddRange(carry.Rounding);
        }
        foreach (var s in data.Sources.Where(s => s.Opening))
        {
            cancellation.ThrowIfCancellationRequested();
            if (s.End != begin.AddDays(-1) || !accounts.Contains(s.Account)) throw new CrsException("期初报告需对应上一年 12 月 31 日及本次活动账户。");
        }
        var issues = data.Issues.Where(i => i.Date is null || i.Date <= end).ToList();
        if (data.Broker == "FUTU")
        {
            // 富途期初市价只证明数量；真实成本须由结转或覆盖此前的成交链提供。
            foreach (var p in data.BeginningPositions.Where(p => p.Quantity != 0))
            {
                var earliest = data.Sources.Where(s => s.Purpose == "AnnualActivity" && s.Account == p.Key.Account).Min(s => s.Start);
                if (p.Date > earliest || p.Date < earliest.AddDays(-1)) continue;
                if (carry is null && !data.OpeningLots.Any(l => l.Key == p.Key))
                    issues.Add(new("MISSING_COST", "期初持仓只有数量和市价，缺少真实 LOT 成本，未使用市价代替。", p.Key.Account, p.Symbol, p.Key.Currency, p.Date, Instrument: p.Key.Instrument, AffectsCost: true));
                if (openingZero && earliest == begin)
                    issues.Add(new("OPENING_CONFLICT", "无持仓确认与富途期初持仓相矛盾。", p.Key.Account, p.Symbol, p.Key.Currency, p.Date, Instrument: p.Key.Instrument, AffectsCost: true));
            }
            if (carry is not null)
                foreach (var p in data.BeginningPositions.Where(p => p.Date == begin.AddDays(-1) || p.Date == begin))
                    if (carry.Lots.Where(l => l.Key == p.Key).Sum(l => l.Quantity) != p.Quantity)
                        issues.Add(new("POSITION_MISMATCH", "结转 LOT 数量与富途期初数量不一致。", p.Key.Account, p.Symbol, p.Key.Currency, p.Date, Instrument: p.Key.Instrument, AffectsCost: true));
        }
        if (!scopeConfirmed) issues.Add(new("SCOPE_UNCONFIRMED", "请确认活动报告未过滤账户、证券或现金类型。"));
        bool positionReconciled = true;
        foreach (var account in accounts)
        {
            cancellation.ThrowIfCancellationRequested();
            var reports = data.Sources.Where(s => !s.Opening && s.Account == account).ToList();
            foreach (var section in new[] { "成交", "现金" })
            {
                // 富途历史成交参与成本时，历史年度之间也必须连续覆盖，不能只检查目标年。
                var next = data.Broker == "FUTU" && carry is null && reports.Any(s => s.Purpose == "AnnualActivity")
                    ? reports.Where(s => s.Purpose == "AnnualActivity").Min(s => s.Start) : begin;
                foreach (var s in reports.Where(s => section == "成交" ? s.HasTrades : s.HasCash).OrderBy(s => s.Start))
                {
                    if (s.End < next) continue;
                    if (s.Start > next) break;
                    next = s.End.AddDays(1);
                    if (next > end) break;
                }
                if (next <= end) issues.Add(new("INCOMPLETE_PERIOD", $"{section}报告未覆盖完整年度。", account, AffectsCost: data.Broker == "FUTU" && section == "成交"));
            }
            if (!openingZero && carry is null && !data.Sources.Any(s => s.Opening && s.Account == account))
                issues.Add(new("OPENING_UNCONFIRMED", "需期初 LOT 或确认期初无持仓。", account));
            if (!reports.Any(s => s.HasPositions && s.End == end))
            { positionReconciled = false; issues.Add(new("MISSING_END_POSITIONS", "缺少年末完整持仓 SUMMARY。", account)); }
        }
        return new(begin, end, accounts, issues, positionReconciled);
    }
}

internal sealed record PreparedAnnualInput(DateOnly Begin, DateOnly End, string[] Accounts,
    List<Issue> Issues, bool PositionReconciled);
