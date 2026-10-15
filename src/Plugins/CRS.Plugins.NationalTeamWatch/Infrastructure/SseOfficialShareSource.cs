using System.Globalization;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>根据上交所官方公开页面脚本核实的ETF份额JSON适配器，不使用金额字段代替份额。</summary>
public sealed class SseOfficialShareSource(IPluginHttpGateway http,IRawEvidenceStore? raw=null):IOfficialShareSource
{
    public static Uri Request(DateOnly date,int page)=>new($"https://query.sse.com.cn/commonQuery.do?sqlId=COMMON_SSE_ZQPZ_ETFZL_XXPL_ETFGM_SEARCH_L&isPagination=true&pageHelp.pageSize=1000&pageHelp.pageNo={page}&pageHelp.beginPage={page}&pageHelp.endPage={page}&pageHelp.cacheSize=1&STAT_DATE={date:yyyy-MM-dd}");
    public static (List<ImportRow> Rows,int PageCount,int Total) Parse(byte[] bytes,DateOnly expected,int expectedPage)
    {
        using var document=JsonDocument.Parse(bytes);var root=document.RootElement;
        foreach(var error in new[]{"actionErrors","actionMessages"})
            if(root.TryGetProperty(error,out var errors) && errors.ValueKind==JsonValueKind.Array && errors.GetArrayLength()>0)throw new InvalidDataException("上交所接口返回错误提示。");
        var help=root.GetProperty("pageHelp");
        static int Number(JsonElement item)=>int.Parse(item.ToString(),CultureInfo.InvariantCulture);
        var total=Number(help.GetProperty("total"));var pages=Number(help.GetProperty("pageCount"));
        if(Number(help.GetProperty("pageNo"))!=expectedPage || pages<0 || pages>20 || total<0 || total>20000)throw new InvalidDataException("上交所分页结构发生变化或超过采集上限。");
        var rows=new List<ImportRow>();var seen=new HashSet<string>();
        foreach(var value in root.GetProperty("result").EnumerateArray())
        {
            var date=DateOnly.ParseExact(value.GetProperty("STAT_DATE").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var code=value.GetProperty("SEC_CODE").GetString()!;
            if(date!=expected || code.Length!=6 || !code.All(char.IsAsciiDigit) || !seen.Add(code))throw new InvalidDataException("上交所日期、代码或重复记录校验失败。");
            var amount=decimal.Parse(value.GetProperty("TOT_VOL").ToString(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
            if(amount<0)throw new InvalidDataException("上交所总份额不能为负数。");
            rows.Add(new(new("SSE",code),date,amount,"万份"));
        }
        if(total==0 && rows.Count>0 || total>0 && (pages==0 || rows.Count==0))throw new InvalidDataException("上交所分页计数与数据不一致。");
        return(rows,pages,total);
    }
    public async Task<SourceDay> FetchAsync(DateOnly day,CancellationToken token)
    {
        var pages=new List<SourcePage>();var allCodes=new HashSet<string>();var count=1;int? total=null;
        for(var page=1;page<=count;page++)
        {
            token.ThrowIfCancellationRequested();var uri=Request(day,page);var response=await http.GetAsync(uri,token);
            // 解析失败和非成功响应也保留字节现场，不能靠重试覆盖原始证据。
            token.ThrowIfCancellationRequested();raw?.Save(response.Content);
            if(response.StatusCode!=200)throw new InvalidDataException($"上交所采集失败：HTTP {response.StatusCode}；未将缺失数据填零。");
            var parsed=Parse(response.Content,day,page);
            if(total is not null && (total!=parsed.Total || count!=Math.Max(1,parsed.PageCount)))throw new InvalidDataException("上交所分页期间总数发生变化，请稍后重试。");
            total=parsed.Total;count=Math.Max(1,parsed.PageCount);
            foreach(var row in parsed.Rows)if(!allCodes.Add(row.Key.Ticker))throw new InvalidDataException("上交所跨页记录重复。");
            pages.Add(new(uri,response.Content,parsed.Rows));
        }
        if(allCodes.Count!=total)throw new InvalidDataException("上交所返回数据截断，拒绝登记为完整日期。");
        return new(day,pages);
    }
}
