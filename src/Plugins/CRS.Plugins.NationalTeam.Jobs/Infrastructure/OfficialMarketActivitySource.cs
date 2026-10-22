using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>交易所股票总计；不叠加子板块，不加入基金、债券或回购。</summary>
public sealed class OfficialMarketActivitySource(IPluginHttpGateway http):IMarketActivitySource
{
    public static Uri Request(string market,DateOnly day)=>market switch
    {
        "SSE"=>new($"https://query.sse.com.cn/commonQuery.do?sqlId=COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C&PRODUCT_CODE=01%2C02%2C03%2C11%2C17&type=inParams&SEARCH_DATE={day:yyyy-MM-dd}"),
        "SZSE"=>new($"https://www.szse.cn/api/report/ShowReport/data?SHOWTYPE=JSON&CATALOGID=scsj_gprdgk_after&TABKEY=tab1&txtQueryDate={day:yyyy-MM-dd}"),
        _=>throw new ArgumentException("不支持的市场。")
    };
    public async Task<MarketActivitySource> FetchAsync(string market,DateOnly day,CancellationToken token)
    {
        var uri=Request(market,day);var response=await http.GetAsync(uri,token);token.ThrowIfCancellationRequested();
        if(response.StatusCode!=200)
        {
            var error=new InvalidDataException("官方成交统计响应失败。");
            if(http is CRS.Plugins.NationalTeam.Jobs.OfficialHttpGateway gateway&&gateway.RetryAt(uri) is {} until)error.Data["SourceRetryAt"]=until;
            throw error;
        }
        return new(uri,response.Content,market,day,Parse(market,day,response.Content));
    }
    private static string Text(JsonElement value)=>value.ToString();
    private static decimal Number(JsonElement value)
    {
        var text=Text(value);
        if(text.Length>80 || !Regex.IsMatch(text,@"^(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?$",RegexOptions.None,TimeSpan.FromMilliseconds(100)))throw new InvalidDataException("成交统计数值格式异常。");
        return decimal.Parse(text,NumberStyles.AllowDecimalPoint|NumberStyles.AllowThousands,CultureInfo.InvariantCulture);
    }
    public static MarketActivityObservation? Parse(string market,DateOnly day,byte[] bytes)
    {
        using var doc=JsonDocument.Parse(bytes);
        if(market=="SSE")
        {
            var root=doc.RootElement;
            if(root.GetProperty("sqlId").GetString()!="COMMON_SSE_SJ_GPSJ_CJGK_MRGK_C" || root.GetProperty("actionErrors").GetArrayLength()!=0)throw new InvalidDataException("沪市成交统计来源异常。");
            var rows=root.GetProperty("result").EnumerateArray().ToArray();
            if(rows.Length==0)return null;
            if(rows.Any(x=>x.GetProperty("TRADE_DATE").GetString()!=day.ToString("yyyyMMdd",CultureInfo.InvariantCulture)))throw new InvalidDataException("沪市成交日期不匹配。");
            var total=rows.Single(x=>x.GetProperty("PRODUCT_CODE").GetString()=="17");
            return new(market,day,checked(Number(total.GetProperty("TRADE_AMT"))*100000000m),checked(Number(total.GetProperty("TRADE_VOL"))*100000000m));
        }
        if(market!="SZSE")throw new ArgumentException("不支持的市场。");
        var table=doc.RootElement.EnumerateArray().Single(x=>x.GetProperty("metadata").GetProperty("catalogid").GetString()=="scsj_gprdgk_after" && x.GetProperty("metadata").GetProperty("tabkey").GetString()=="tab1");
        if(table.GetProperty("error").ValueKind!=JsonValueKind.Null)throw new InvalidDataException("深市成交统计来源异常。");
        var metadata=table.GetProperty("metadata");
        if(metadata.GetProperty("cols").GetProperty("gp").GetString()!="深市合计" || metadata.GetProperty("conditions").EnumerateArray().Single(x=>x.GetProperty("name").GetString()=="txtQueryDate").GetProperty("defaultValue").GetString()!=day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))throw new InvalidDataException("深市成交日期或统计范围不匹配。");
        var data=table.GetProperty("data").EnumerateArray().ToArray();if(data.Length==0)return null;
        var amount=data.Single(x=>x.GetProperty("zbmc").GetString()=="成交金额（亿元）");
        var volume=data.Single(x=>x.GetProperty("zbmc").GetString()=="成交量（亿）");
        return new(market,day,checked(Number(amount.GetProperty("gp"))*100000000m),checked(Number(volume.GetProperty("gp"))*100000000m));
    }
}
