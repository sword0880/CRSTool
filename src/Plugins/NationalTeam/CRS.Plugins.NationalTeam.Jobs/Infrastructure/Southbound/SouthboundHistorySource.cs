using System.Globalization;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>固定南向沪深买卖、净买额及累计额；完整分页后交由领域核对口径，不读取额度净流入。</summary>
public static class SouthboundHistorySource
{
    public const int PageSize=2000;
    public const string ProviderPage="https://data.eastmoney.com/hsgt/index.html";
    public static Uri PageUri(DateOnly from,DateOnly through,int page)
    {
        if(from<SouthboundAnnualComparison.SourceFrom||through<from||through.Year-from.Year>19||page is <1 or >5)
            throw new InvalidDataException("南向历史请求超出范围。");
        var filter=$"(MUTUAL_TYPE in (\"002\",\"004\"))(TRADE_DATE>='{from:yyyy-MM-dd}')(TRADE_DATE<='{through:yyyy-MM-dd}')";
        return new("https://datacenter-web.eastmoney.com/api/data/v1/get?reportName=RPT_MUTUAL_DEAL_HISTORY&columns=MUTUAL_TYPE,TRADE_DATE,BUY_AMT,SELL_AMT,NET_DEAL_AMT,ACCUM_DEAL_AMT&pageSize=2000&pageNumber="
            +page+"&sortColumns=TRADE_DATE,MUTUAL_TYPE&sortTypes=1,1&filter="+Uri.EscapeDataString(filter));
    }
    public static bool IsSource(Uri uri)
    {
        if(uri.Scheme!="https"||uri.Host!="datacenter-web.eastmoney.com"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!="/api/data/v1/get")return false;
        var match=System.Text.RegularExpressions.Regex.Match(Uri.UnescapeDataString(uri.Query),@"pageNumber=([1-5])&.*TRADE_DATE>='(\d{4}-\d{2}-\d{2})'\)\(TRADE_DATE<='(\d{4}-\d{2}-\d{2})'\)$",System.Text.RegularExpressions.RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(!match.Success||!DateOnly.TryParseExact(match.Groups[2].Value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)
            ||!DateOnly.TryParseExact(match.Groups[3].Value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var through))return false;
        try{return uri.AbsoluteUri==PageUri(from,through,int.Parse(match.Groups[1].Value,CultureInfo.InvariantCulture)).AbsoluteUri;}
        catch(InvalidDataException){return false;}
    }
    public sealed record ParsedPage(int Pages,int Count,IReadOnlyList<SouthboundFlowObservation> Rows);
    public static ParsedPage ParsePage(byte[] bytes,DateOnly from,DateOnly through)
    {
        if(bytes.Length is 0 or >2*1024*1024)throw new InvalidDataException("南向历史原件大小无效。");
        try
        {
            using var doc=JsonDocument.Parse(bytes,new(){MaxDepth=16});var root=doc.RootElement;
            if(!root.GetProperty("success").GetBoolean()||root.GetProperty("code").GetInt32()!=0)throw new InvalidDataException("南向历史来源未返回成功报表。");
            var result=root.GetProperty("result");var pages=result.GetProperty("pages").GetInt32();var count=result.GetProperty("count").GetInt32();
            if(pages is <1 or >5||count is <1 or >10000||pages!=(count+PageSize-1)/PageSize)throw new InvalidDataException("南向历史分页限额或总数无效。");
            var rows=new List<SouthboundFlowObservation>();
            foreach(var item in result.GetProperty("data").EnumerateArray())
            {
                var channel=item.GetProperty("MUTUAL_TYPE").GetString()!;
                if(!DateTime.TryParseExact(item.GetProperty("TRADE_DATE").GetString(),"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)||day.TimeOfDay!=TimeSpan.Zero)
                    throw new InvalidDataException("南向历史日期格式无效。");
                var date=DateOnly.FromDateTime(day);
                decimal? ReadAmount(string field){var amount=item.GetProperty(field);return amount.ValueKind==JsonValueKind.Null?null:amount.GetDecimal();}
                if(channel is not ("002" or "004")||date<from||date>through)throw new InvalidDataException("南向历史通道或日期范围无效。");
                rows.Add(new(date,channel,ReadAmount("BUY_AMT"),ReadAmount("SELL_AMT"),ReadAmount("NET_DEAL_AMT"),ReadAmount("ACCUM_DEAL_AMT")));
            }
            if(rows.Count is <1 or >PageSize)throw new InvalidDataException("南向历史单页数量无效。");
            SouthboundAnnualComparison.ValidateObservations(rows,through);
            return new(pages,count,rows);
        }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {throw new InvalidDataException("南向历史数据格式改变，未返回部分数据。",error);}
    }
    public static IReadOnlyList<SouthboundFlowObservation> ParseCapture(SouthboundCapture capture)
    {
        _=PageUri(capture.From,capture.Through,1);
        if(capture.Pages.Count is <1 or >5)throw new InvalidDataException("南向历史快照缺少完整分页。");
        var rows=new List<SouthboundFlowObservation>();int? expected=null;
        for(var i=0;i<capture.Pages.Count;i++)
        {
            var page=capture.Pages[i];
            if(page.Url!=PageUri(capture.From,capture.Through,i+1).AbsoluteUri)throw new InvalidDataException("南向历史来源或页码不匹配。");
            var parsed=ParsePage(page.Content,capture.From,capture.Through);
            if(parsed.Pages!=capture.Pages.Count||expected.HasValue&&expected!=parsed.Count
                ||parsed.Rows.Count!=(i==parsed.Pages-1?parsed.Count-i*PageSize:PageSize))throw new InvalidDataException("南向历史分页发生变化或提前结束。");
            expected=parsed.Count;rows.AddRange(parsed.Rows);
        }
        if(rows.Count!=expected||rows.Select(x=>(x.Date,x.Channel)).Distinct().Count()!=rows.Count
            ||!rows.SequenceEqual(rows.OrderBy(x=>x.Date).ThenBy(x=>x.Channel,StringComparer.Ordinal)))throw new InvalidDataException("南向历史分页重复、缺行或排序不一致。");
        return rows;
    }
}
