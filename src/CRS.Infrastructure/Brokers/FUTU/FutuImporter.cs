using CRS.Domain;
using CRS.Application.Abstractions;
using CRS.Infrastructure.Pdf;

namespace CRS.Infrastructure;

/// <summary>富途输入组合适配器：唯一收入主表加不重叠的年度交易工作簿。</summary>
public sealed class FutuImporter(int year) : IBrokerImporter
{
    internal const string ProjectionVersion="FUTU.Tax.v1";
    internal Action<FrozenInput,CancellationToken,List<string>,BrokerSourceDocument?>? SharedImport {get;init;}
    /// <summary>识别交易和收入用途，绑定可靠账户，拒绝重叠主数据且保留缺失资料问题。</summary>
    public ImportData Parse(IEnumerable<string> files, string? opening = null, CancellationToken cancellation = default)
    {
        if (opening is not null) throw new CrsException("富途不能使用 IBKR 期初 XML，请提供 C# 上年 LOT 结转或完整历史交易。");
        var paths = files.ToArray(); if (paths.Length is 0 or > 36) throw new CrsException("请选择 1 至 36 份富途收入／年度交易文件。");
        if (paths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new CrsException("富途源文件名重复，请保留唯一文件名以便逐行追溯。");
        var data = new ImportData { Broker = "FUTU" }; FrozenInput? incomeInput=null; var incomeIsPdf=false;
        var nameMap = new Dictionary<string, HashSet<string>>();
        void SetIncome(FrozenInput input,bool pdf)
        {
            // 最多保留一份收入原件的内存副本，重复收入立即拒绝，避免累积全部文件字节。
            if(incomeInput is not null) throw new CrsException("请选择且仅选择一份富途年度收入主表（Excel 或 PDF）；两种格式不能重复计入。");
            incomeInput=input; incomeIsPdf=pdf;
        }
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pdf") { SetIncome(FrozenInput.Read(path,cancellation),true); continue; }
            if (extension != ".xlsx") throw new CrsException("富途仅支持券商原始 XLSX 和可提取文字的收入 PDF。");
            var input=FrozenInput.Read(path,cancellation); using var table = new FutuTable(input);
            if (table.Read("交易流水", false) is not null)
            {
                var map = new FutuTradeExcelImporter().Read(input,table, data, year, cancellation);
                SharedImport?.Invoke(input,cancellation,data.Warnings,null);
                RecordVersion(data,input,FutuTradeExcelImporter.ReaderVersion);
                foreach (var (name, ids) in map) { if (!nameMap.TryGetValue(name, out var existing)) nameMap[name] = existing = []; existing.UnionWith(ids); }
            }
            else SetIncome(input,false);
        }
        if (incomeInput is null) throw new CrsException("请选择且仅选择一份富途年度收入主表（Excel 或 PDF）；两种格式不能重复计入。");
        var file=incomeInput.Name;
        var rows = incomeIsPdf ? FutuDividendPdfImporter.Read(incomeInput, cancellation) : FutuDividendExcelImporter.Read(incomeInput);
        foreach (var row in rows)
        {
            cancellation.ThrowIfCancellationRequested(); var name = row["账户名称"] == "" ? row["Account"] : row["账户名称"];
            if (name == "") continue;
            string account;
            if (row["账户号码"] != "") account = "FUTU:" + row["账户号码"];
            else if (nameMap.TryGetValue(name, out var ids) && ids.Count == 1) account = ids.Single();
            else
            {
                // 收入表只有展示名称时保持独立身份；不把不同名称／语言的账户强行合并。
                account = "FUTU:INCOME:" + row["牛牛号"] + ":" + name;
                if (data.Sources.Any(s => s.Purpose == "AnnualActivity")) data.Issues.Add(new("INCOME_ACCOUNT_UNCONFIRMED", "收入账户名称无法唯一绑定交易账户，请核对账户归属。", account, File: file));
            }
            var income = FutuDividendExcelImporter.Parse(row, file, year, account, data);
            if (income.Year != year) continue;
            if (data.AnnualIncome.Any(i => i.Account == account && i.Currency == income.Currency && i.Year == income.Year)) throw new CrsException("年度收入表同账户同币种存在重复主数据，请核对，不能直接累加。");
            data.AnnualIncome.Add(income);
        }
        if (data.AnnualIncome.Count == 0) throw new CrsException("收入主表没有所选年度的有效记录。");
        var hash = incomeInput.Sha256.ToUpperInvariant();
        foreach (var account in data.AnnualIncome.Select(i => i.Account).Distinct()) data.Sources.Add(new(file, hash, account, new(year, 1, 1), new(year, 12, 31), false, false, false, Purpose: "AnnualIncomeSummary"));
        foreach (var account in data.Sources.Where(s => s.Purpose == "AnnualActivity" && s.Start.Year == year).Select(s => s.Account).Distinct().ToArray())
            if (!data.AnnualIncome.Any(i => i.Account == account)) data.Issues.Add(new("MISSING_ACCOUNT_INCOME", "交易账户没有可唯一绑定的年度收入记录，不能视为零收入。", account));
        if (!data.Sources.Any(s => s.Purpose == "AnnualActivity" && s.Start.Year == year)) data.Issues.Add(new("MISSING_TRADE_FILE", "未提供所选年度交易流水；仅测算股息利息，资本收益和预扣税未确认。"));
        if (data.SecurityMovements.Count > 0) data.Warnings.Add($"保留 {data.SecurityMovements.Count} 条证券资产事件待复核，未自动调整成本。");
        if (data.Issues.Any(i => i.Code == "UNSUPPORTED_ASSET")) data.Warnings.Add("未支持的基金或其他品类已明确列于待复核清单，未作为零收益。");
        data.Warnings.Add("富途年度汇总为收入主数据；资金进出派息明细仅供核对，未重复计入股息。富途报表未提供逐笔券商 FIFO 盈亏，全部对账仍需人工复核。");
        RecordVersion(data,incomeInput,incomeIsPdf?FutuDividendPdfImporter.ReaderVersion:FutuDividendExcelImporter.ReaderVersion);
        return data;
    }
    private static void RecordVersion(ImportData data,FrozenInput input,string reader)
    {
        data.ImportProvenance??=[];
        data.ImportProvenance.Add(new(1,"FUTU",input.Sha256,input.Sha256,reader,ProjectionVersion));
    }
}
