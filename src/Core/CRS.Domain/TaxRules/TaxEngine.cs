namespace CRS.Domain;

public static class TaxEngine
{
    // 新增抵免证据门槛后旧快照不再声称与当前结果等价重放。
    public const string PolicyVersion = "V2-2026-10";
    // 此处迁移既有测算口径；规则是否适用于实际申报仍由独立复核确认。
    /// <summary>迁移年度收入、资本收益与净预扣税的现有测算规则；亏损不产生资本收益税。</summary>
    public static TaxSummary Calculate(IEnumerable<Income> income, IEnumerable<Match> matches,
        IReadOnlyDictionary<string, decimal> withholding, int year, Func<int, string, decimal> rate)
    {
        decimal dividend = 0, interest = 0, paid = 0;
        foreach (var i in income.Where(i => i.Year == year))
        {
            dividend += FifoEngine.Money(i.Dividend * rate(year, i.Currency));
            interest += FifoEngine.Money(i.Interest * rate(year, i.Currency));
        }
        foreach (var (currency, value) in withholding) paid += FifoEngine.Money(value * rate(year, currency));
        var incomeTax = FifoEngine.Money((dividend + interest) * .20m);
        var credit = Math.Max(Math.Min(paid, incomeTax), 0);
        var gain = matches.Where(m => m.SellDate.Year == year).Sum(m => m.GainCny);
        var gainTax = FifoEngine.Money(Math.Max(gain, 0) * .20m);
        return new(dividend, interest, gain, incomeTax, gainTax, credit, Math.Max(incomeTax - credit, 0) + gainTax);
    }
}
