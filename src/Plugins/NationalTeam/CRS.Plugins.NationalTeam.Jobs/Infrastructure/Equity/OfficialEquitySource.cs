using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>官网公司页当前十大股东XBRL；REPORT_DATE仅作来源日期，人工核对PDF后才能成为报告期持仓。</summary>
public static class OfficialEquitySource
{
    public static Uri Request(string market,string ticker)
    {
        if(market!="SSE" || ticker.Length!=6 || !ticker.All(char.IsAsciiDigit))throw new InvalidDataException("当前自动股东适配器仅核验沪市；深市来源尚未核验。");
        return new($"https://query.sse.com.cn/commonSoaQuery.do?isPagination=false&sqlId=COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS&stockId={ticker}");
    }
    public static (DateOnly SourceDate,IReadOnlyList<EquityHolder> Holders) Parse(string ticker,byte[] bytes)
    {
        using var doc=JsonDocument.Parse(bytes);var root=doc.RootElement;
        if(root.GetProperty("sqlId").GetString()!="COMMON_SSE_PL_XBRL_TOP10SHAREHOLDERS" || root.GetProperty("actionErrors").GetArrayLength()!=0)throw new InvalidDataException("股东来源标识或状态错误。");
        var rows=root.GetProperty("result");
        if(rows.ValueKind!=JsonValueKind.Array || rows.GetArrayLength() is <1 or >10)throw new InvalidDataException("股东报表未发布或范围超限。");
        if(root.GetProperty("pageHelp").GetProperty("total").GetInt32()!=rows.GetArrayLength())throw new InvalidDataException("十大股东结果截断。");
        var holders=new List<EquityHolder>();DateOnly? sourceDate=null;
        foreach(var row in rows.EnumerateArray())
        {
            var date=DateOnly.ParseExact(row.GetProperty("REPORT_DATE").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(row.GetProperty("STOCK_ID").GetString()!=ticker || sourceDate is not null && date!=sourceDate)throw new InvalidDataException("股东日期或代码不匹配。");
            sourceDate=date;var name=row.GetProperty("NAME").GetString()?.Trim();var shares=row.GetProperty("NUMBER_END").GetDecimal();var percent=row.GetProperty("RATIO").GetDecimal();
            if(string.IsNullOrWhiteSpace(name)||name.Length>200||shares<0||decimal.Truncate(shares)!=shares||percent<0||percent>100||holders.Any(x=>x.Account==name))throw new InvalidDataException("股东名称、持股或比例异常。");
            holders.Add(new(name,shares,percent));
        }
        return(sourceDate!.Value,holders);
    }
}
