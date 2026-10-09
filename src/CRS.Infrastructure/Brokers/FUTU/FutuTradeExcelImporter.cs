using System.Security.Cryptography;
using CRS.Domain;

namespace CRS.Infrastructure;

/// <summary>迁移富途年度交易、持仓及资产现金事件，不从持仓市价生成成本。</summary>
internal sealed class FutuTradeExcelImporter
{
    /// <summary>读取一个年度工作簿，返回可用于绑定收入账户的名称与号码关系。</summary>
    public Dictionary<string, HashSet<string>> Read(string path, ImportData data, int selectedYear, CancellationToken cancellation)
    {
        using var table = new FutuTable(path);
        var accounts = table.Read("账户信息", true, "年份", "账户号码", "账户名称")!;
        if (accounts.Count == 0) throw new CrsException("富途账户信息没有年度和账户记录；只有表头的文件不能证明全年零交易。");
        var years = accounts.Select(r => FutuFields.Year(r["年份"])).Distinct().ToArray();
        if (years.Length != 1) throw new CrsException("一份富途年度交易工作簿必须明确且仅含一个报告年度。");
        var reportYear = years[0]; if (reportYear > selectedYear) throw new CrsException("富途交易报告晚于所选年度。");
        var accountIds = accounts.Select(r => Account(r)).Distinct().ToHashSet();
        var names = accounts.GroupBy(r => r["账户名称"]).ToDictionary(g => g.Key, g => g.Select(Account).ToHashSet());
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var file = Path.GetFileName(path);
        if (data.Sources.Any(s => s.Purpose == "AnnualActivity" && s.Sha256 == hash)) throw new CrsException("同一富途交易文件被重复选择，不能重复计入。");
        if (data.Sources.Any(s => s.Purpose == "AnnualActivity" && s.Start.Year == reportYear && accountIds.Contains(s.Account)))
            throw new CrsException("同账户同年度富途交易来源重叠；缺少稳定成交 ID，不能安全自动去重，请选择唯一完整报表。");
        var trades = table.Read("交易流水", true, "成交时间", "品类", "代码名称", "方向", "数量/面值", "价格", "币种", "账户号码", "交易所/市场")!;
        foreach (var row in trades)
        {
            cancellation.ThrowIfCancellationRequested();
            var account = ValidateAccount(row); var time = FutuFields.Time(row["成交时间"]); var date = DateOnly.FromDateTime(time.DateTime);
            if (time.Year != reportYear) throw new CrsException($"【{row.Sheet}】第 {row.Row} 行成交日期与报告年度不一致。");
            var symbol = row["代码名称"].ToUpperInvariant(); var currency = FutuFields.Currency(row["币种"]);
            var key = FutuFields.Key(account, symbol, currency, row["交易所/市场"]); var id = $"{hash}:{row.Sheet}:{row.Row}";
            if (key.Instrument == "" || key.Market == "") throw new CrsException($"【{row.Sheet}】第 {row.Row} 行缺少证券代码或市场。");
            if (row["品类"] != "证券")
            { data.Issues.Add(new("UNSUPPORTED_ASSET", $"第 {row.Row} 行品类【{row["品类"]}】尚未支持，未计入 FIFO。", account, symbol, currency, date, file, id)); continue; }
            var side = row["方向"].ToUpperInvariant() switch { "买入开仓" or "买入" or "BUY" => "BUY", "卖出平仓" or "卖出" or "SELL" => "SELL", _ => throw new CrsException($"第 {row.Row} 行无法识别买卖方向。") };
            var rawQuantity = FutuFields.Money(row["数量/面值"], "数量/面值"); var quantity = Math.Abs(rawQuantity);
            if (quantity == 0) { data.Issues.Add(new("ZERO_QUANTITY", "成交数量为零或缺失。", account, symbol, currency, date, file, id, key.Instrument, true)); continue; }
            if (side == "BUY" && rawQuantity < 0 || side == "SELL" && rawQuantity > 0)
                data.Issues.Add(new("QUANTITY_SIGN", "数量符号与买卖方向不一致，已保留绝对数量供复核。", account, symbol, currency, date, file, id, key.Instrument, true));
            var price = FutuFields.Money(row["价格"], "价格"); if (price <= 0) throw new CrsException($"第 {row.Row} 行成交价格必须大于零。");
            if (row["交收日期"] is not ("" or "-")) _ = FutuFields.Date(row["交收日期"]);
            // 总费用已经包含佣金、平台费和征费，不再次累加额外明细列。
            var fee = Math.Abs(FutuFields.Money(row["总费用"], "总费用"));
            if (!row.Values.ContainsKey("总费用") || row["总费用"] is "" or "-" or "—")
                data.Issues.Add(new("MISSING_TRADE_FEES", "总费用缺失，暂按零重建但不能视为完整成本。", account, symbol, currency, date, file, id, key.Instrument, true));
            var expectedGross = FifoEngine.Money(quantity * price) * (side == "BUY" ? -1 : 1);
            var gross = FutuFields.OptionalMoney(row["成交金额"], "成交金额"); var net = FutuFields.OptionalMoney(row["变动金额"], "变动金额");
            var expectedNet = (gross ?? expectedGross) - fee;
            var mismatch = gross is not null && Math.Abs(gross.Value - expectedGross) > .01m || net is not null && Math.Abs(net.Value - expectedNet) > .01m;
            var status = mismatch ? "存在差异" : gross is null || net is null ? "缺少原始金额" : "一致";
            data.TradeAmountChecks.Add(new(account, symbol, currency, id, expectedGross, gross, expectedNet, net, status, file, row.Sheet, row.Row));
            if (status != "一致") data.Issues.Add(new(mismatch ? "TRADE_AMOUNT_MISMATCH" : "TRADE_AMOUNT_MISSING", $"第 {row.Row} 行原始成交／变动金额核对：{status}。", account, symbol, currency, date, file, id, key.Instrument, mismatch));
            data.Trades.Add(new(key, symbol, time, false, side, quantity, price, Math.Abs(gross ?? expectedGross), fee, net ?? expectedNet, id, file, row.Row, "STK", null));
        }
        var positions = table.Read("持仓总览", false, "时期类型", "日期", "品类", "账户号码", "代码名称", "交易所/市场", "币种", "数量/面值");
        foreach (var row in positions ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            var account = ValidateAccount(row); var date = FutuFields.Date(row["日期"]); var symbol = row["代码名称"].ToUpperInvariant();
            if (row["品类"] != "证券") { data.Issues.Add(new("UNSUPPORTED_POSITION", "持仓含未支持品类。", account, symbol, Date: date, File: file)); continue; }
            var key = FutuFields.Key(account, symbol, row["币种"], row["交易所/市场"]); var quantity = FutuFields.Money(row["数量/面值"], "数量/面值");
            if (key.Instrument == "" || key.Market == "") throw new CrsException("富途持仓缺少证券代码或市场。");
            if (quantity < 0) data.Issues.Add(new("UNSUPPORTED_POSITION", "不支持空头持仓成本。", account, symbol, key.Currency, date, file, Instrument: key.Instrument, AffectsCost: true));
            if (row["时期类型"] == "期末" && date == new DateOnly(reportYear, 12, 31)) data.Positions.Add(new(key, symbol, date, quantity));
            else if (row["时期类型"] == "期初" && (date == new DateOnly(reportYear - 1, 12, 31) || date == new DateOnly(reportYear, 1, 1))) data.BeginningPositions.Add(new(key, symbol, date, quantity));
            else data.Issues.Add(new("POSITION_PERIOD", "持仓日期或时期类型与报告年度不一致。", account, symbol, key.Currency, date, file));
        }
        var movements = table.Read("资产进出", false, "日期", "账户号码", "代码名称", "交易所/市场", "币种", "数量", "方向", "类型", "备注");
        foreach (var row in movements ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            // 未支持事件保留原币种代码；如 IVD 并非可计税币种，不调用汇率或猜测转换。
            var rawCurrency = row["币种"].ToUpperInvariant();
            var key = new SecurityKey(ValidateAccount(row), row["代码名称"].ToUpperInvariant(), rawCurrency, "FUTU", row["交易所/市场"].ToUpperInvariant()); var date = FutuFields.Date(row["日期"]);
            data.SecurityMovements.Add(new(key, row["代码名称"], date, row["类型"], row["方向"], FutuFields.Money(row["数量"], "数量"), row["备注"], file, row.Sheet, row.Row));
            // 与 Python 保守口径一致：仅同日 In/Out 不能证明可信更名或成本守恒。
            data.Issues.Add(new("UNSUPPORTED_TRANSFER", "资产进出或公司行动需核对证券身份及真实成本；未自动更名或补造零成本。", key.Account, row["代码名称"],
                rawCurrency is "USD" or "HKD" or "CNY" ? rawCurrency : "", date, file, $"{row.Sheet}:{row.Row}", key.Instrument, true));
        }
        var funds = table.Read("资金进出", false, "日期", "账户号码", "类型", "方向", "币种", "变动金额", "备注");
        foreach (var row in funds ?? [])
        {
            cancellation.ThrowIfCancellationRequested();
            var account = ValidateAccount(row); var date = FutuFields.Date(row["日期"]); var currency = FutuFields.Currency(row["币种"]);
            if (date.Year != reportYear) throw new CrsException("资金进出日期与报告年度不一致。");
            var amount = FutuFields.OptionalMoney(row["变动金额"], "变动金额") ?? throw new CrsException($"资金进出第 {row.Row} 行金额缺失，不能视为零。");
            var description = row["备注"]; var type = description.Contains("withholding tax", StringComparison.OrdinalIgnoreCase) ? "Withholding Tax" : row["类型"];
            data.Cash.Add(new(account, date, type, currency, amount, $"{hash}:{row.Sheet}:{row.Row}", file));
            data.FundMovements.Add(new(account, row["账户名称"], date, currency, row["类型"], row["方向"], amount, description, DividendSymbol(description), file, row.Sheet, row.Row));
        }
        if (funds is null) data.Issues.Add(new("MISSING_CASH_FLOWS", "缺少资金进出表，预扣税和现金收入覆盖未确认。", File: file));
        var balances = table.Read("资金总览", false, "时期类型", "日期", "账户号码", "币种", "金额");
        foreach (var account in accountIds)
        {
            data.Sources.Add(new(file, hash, account, new(reportYear, 1, 1), new(reportYear, 12, 31), true, funds is not null, positions is not null));
            foreach (var group in (balances ?? []).Where(r => Account(r) == account).GroupBy(r => FutuFields.Currency(r["币种"])))
            {
                var start = group.Where(r => r["时期类型"] == "期初" && FutuFields.Date(r["日期"]) == new DateOnly(reportYear - 1, 12, 31)).ToList();
                var end = group.Where(r => r["时期类型"] == "期末" && FutuFields.Date(r["日期"]) == new DateOnly(reportYear, 12, 31)).ToList();
                if (start.Count > 1 || end.Count > 1) throw new CrsException("富途资金总览存在重复余额。");
                decimal? beginAmount = start.Count == 1 ? FutuFields.Money(start[0]["金额"], "期初现金") : null;
                decimal? endAmount = end.Count == 1 ? FutuFields.Money(end[0]["金额"], "期末现金") : null;
                var change = data.Trades.Where(t => t.File == file && t.Key.Account == account && t.Key.Currency == group.Key).Sum(t => t.Net)
                    + data.Cash.Where(c => c.File == file && c.Account == account && c.Currency == group.Key).Sum(c => c.Amount);
                var difference = beginAmount + change - endAmount; var status = funds is null || difference is null ? "缺少资料" : Math.Abs(difference.Value) <= .02m ? "一致" : "存在差异";
                data.CashChecks.Add(new(account, group.Key, new(reportYear, 1, 1), new(reportYear, 12, 31), beginAmount, change, beginAmount + change, endAmount, difference, status, "按证券成交净额及资金进出核对；不支持品类可能造成差额。", file));
            }
        }
        return names;

        /// <summary>要求每条记录的账户在账户信息主表中有明确归属。</summary>
        string ValidateAccount(FutuRow row)
        {
            var id = Account(row); if (!accountIds.Contains(id)) throw new CrsException($"【{row.Sheet}】第 {row.Row} 行账户未列于账户信息。");
            return id;
        }
    }
    /// <summary>用完整账户号码隔离富途库存，缺失时拒绝跨账户猜测。</summary>
    private static string Account(FutuRow row) => row["账户号码"] == "" ? throw new CrsException("富途交易记录缺少账户号码。"): "FUTU:" + row["账户号码"];
    /// <summary>从现金派息备注提取展示证券；该明细不重新计入年度收入。</summary>
    private static string DividendSymbol(string text)
    {
        var bracket = System.Text.RegularExpressions.Regex.Match(text, @"<[A-Z]+\s+(\d+\s+.+?)>");
        if (bracket.Success) return bracket.Groups[1].Value;
        var us = System.Text.RegularExpressions.Regex.Match(text, @"^([A-Z]+)\s+[\d.]+\s+SHARES?\s+");
        return us.Success ? us.Groups[1].Value : text[..Math.Min(30, text.Length)];
    }
}
