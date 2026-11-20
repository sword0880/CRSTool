using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>按两所实际基金公告协议读取目录；与股票公告接口分离，不推断持有人或报告基准日。</summary>
public static class OfficialEtfReportIndex
{
    public const int MaximumDocuments=1000,MaximumPageBytes=2*1024*1024,MaximumCaptureBytes=8*1024*1024;
    public const string SseSqlId="COMMON_PL_JJXX_JJGG_NEW_L";
    public const string ParserVersion=EtfReportIndexSnapshot.Version;
    public static Uri SzseEndpoint {get;}=new("https://www.szse.cn/api/disc/announcement/annList");
    public static int PageSize(string market)=>market switch{"SSE"=>25,"SZSE"=>50,_=>throw new InvalidDataException("不支持的ETF市场。")};
    public static Uri Request(EtfKey key,DateOnly from,DateOnly through,int page)
    {
        Validate(key,from,through,page);
        return key.Market=="SZSE"?SzseEndpoint:new(FormattableString.Invariant($"https://query.sse.com.cn/commonQuery.do?isPagination=true&pageHelp.pageSize=25&pageHelp.pageNo={page}&pageHelp.beginPage={page}&pageHelp.cacheSize=1&pageHelp.endPage={page}&type=inParams&sqlId={SseSqlId}&TITLE=&SECURITY_CODE={key.Ticker}&BULLETIN_TYPE=reits03%2Cfund03&START_DATE={from:yyyy-MM-dd}&END_DATE={through:yyyy-MM-dd}&DATE_DESC=1&DATE_ASC=&CODE_DESC=&CODE_ASC="));
    }
    public static byte[]? RequestBody(EtfKey key,DateOnly from,DateOnly through,int page)
    {
        Validate(key,from,through,page);
        return key.Market=="SSE"?null:JsonSerializer.SerializeToUtf8Bytes(new{seDate=new[]{from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),through.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)},stock=new[]{key.Ticker},
            channelCode=new[]{"fundinfoNotice_disc"},bigCategoryId=new[]{"yearreport","halfyearreport","quarterreport"},pageSize=50,pageNum=page});
    }
    public static bool IsSseRequest(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Host=="query.sse.com.cn"&&uri.AbsolutePath=="/commonQuery.do"&&uri.Query.Contains("sqlId="+SseSqlId,StringComparison.Ordinal);
    public static void ValidatePost(Uri uri,byte[] body)
    {
        if(uri.AbsoluteUri!=SzseEndpoint.AbsoluteUri||body.Length is 0 or >4096)throw new InvalidDataException("仅允许已核对的基金目录搜索POST。");
        try{ValidatePostBody(body);}
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {throw new InvalidDataException("基金目录POST字段格式无效。",error);}
    }
    private static void ValidatePostBody(byte[] body)
    {
        using var json=JsonDocument.Parse(body);Unique(json.RootElement);var root=json.RootElement;
        var dates=root.GetProperty("seDate");var stocks=root.GetProperty("stock");
        if(dates.ValueKind!=JsonValueKind.Array||dates.GetArrayLength()!=2||stocks.ValueKind!=JsonValueKind.Array||stocks.GetArrayLength()!=1)throw new InvalidDataException("基金目录搜索参数无效。");
        var key=new EtfKey("SZSE",stocks[0].GetString()!);var from=Date(dates[0].GetString()!);var to=Date(dates[1].GetString()!);var page=root.GetProperty("pageNum").GetInt32();
        using var expected=JsonDocument.Parse(RequestBody(key,from,to,page)!);
        if(!JsonElement.DeepEquals(root,expected.RootElement))throw new InvalidDataException("基金目录POST字段或范围不符合已核协议。");
    }
    public static EtfReportIndexPage Parse(EtfKey key,DateOnly from,DateOnly through,int page,byte[] bytes,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        Validate(key,from,through,page);
        if(bytes.Length is 0 or >MaximumPageBytes)throw new InvalidDataException("基金目录响应为空或超过限额。");
        try
        {
            using var json=JsonDocument.Parse(bytes,new(){MaxDepth=24});var root=json.RootElement;Unique(root,token);
            var result=key.Market=="SSE"?Sse(key,from,through,page,root):Szse(key,from,through,page,root);
            token.ThrowIfCancellationRequested();return result;
        }
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {throw new InvalidDataException("官方基金目录字段不符合已核协议。",error);}
    }
    private static EtfReportIndexPage Sse(EtfKey key,DateOnly from,DateOnly through,int page,JsonElement root)
    {
        if(Text(root,"sqlId")!=SseSqlId||Text(root,"type")!="inParams"||Text(root,"isPagination")!="true"
            ||root.GetProperty("actionErrors").GetArrayLength()!=0||root.GetProperty("actionMessages").GetArrayLength()!=0||root.GetProperty("fieldErrors").EnumerateObject().Any())throw new InvalidDataException("上交所返回错误或不是基金公告目录。");
        var help=root.GetProperty("pageHelp");var total=help.GetProperty("total").GetInt32();var rows=root.GetProperty("result");var count=Count(total,25,page,rows);
        if(help.GetProperty("pageCount").GetInt32()!=(total+24)/25||help.GetProperty("pageNo").GetInt32()!=page||help.GetProperty("pageSize").GetInt32()!=25
            ||!JsonElement.DeepEquals(help.GetProperty("data"),rows))throw new InvalidDataException("基金目录分页或重复数据区不一致。");
        var result=new List<EtfReportIndexDocument>();
        foreach(var row in rows.EnumerateArray())
        {
            var date=Date(Text(row,"SSEDATE"));var path=Text(row,"URL");var kind=Text(row,"ORG_BULLETIN_TYPE_DESC");
            if(Text(row,"SECURITY_CODE")!=key.Ticker||Text(row,"BULLETIN_TYPE_DESC")!="定期报告(基金)"||Text(row,"NUM")!=((page-1)*25+result.Count+1).ToString(CultureInfo.InvariantCulture)
                ||!Regex.IsMatch(path,FormattableString.Invariant($@"^/disclosure/fund/announcement/c/new/{date:yyyy-MM-dd}/{key.Ticker}_{date:yyyyMMdd}_[A-Za-z0-9]+\.pdf$"),RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new InvalidDataException("基金目录证券、类型、行号或PDF路径不匹配。");
            var title=Text(row,"TITLE");if(kind is not ("年度报告" or "中期报告" or "半年度报告" or "季度报告"))throw new InvalidDataException("未知基金定期报告分类。");
            Add(result,new(path,title,kind,date,Text(row,"SSEDATE"),new Uri(new Uri("https://www.sse.com.cn"),path).AbsoluteUri,[key.Ticker]),from,through);
        }
        return new(total,count,result.AsReadOnly());
    }
    private static EtfReportIndexPage Szse(EtfKey key,DateOnly from,DateOnly through,int page,JsonElement root)
    {
        var total=root.GetProperty("announceCount").GetInt32();var rows=root.GetProperty("data");var count=Count(total,50,page,rows);var result=new List<EtfReportIndexDocument>();
        if(root.TryGetProperty("recordCount",out var records)&&records.ValueKind!=JsonValueKind.Null&&records.GetInt32()!=total)throw new InvalidDataException("深交所目录总数不一致。");
        foreach(var row in rows.EnumerateArray())
        {
            var id=Text(row,"id");var rawTime=Text(row,"publishTime");
            if(!Guid.TryParseExact(id,"D",out _)||row.GetProperty("annId").GetInt64()<=0||!DateTime.TryParseExact(rawTime,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var stamp))throw new InvalidDataException("深交所公告身份或原时间无效。");
            var date=DateOnly.FromDateTime(stamp);var path=Text(row,"attachPath");var codes=row.GetProperty("secCode");
            if(codes.ValueKind!=JsonValueKind.Array||codes.GetArrayLength() is <1 or >20)throw new InvalidDataException("公告证券数组无效。");
            var securities=codes.EnumerateArray().Select(x=>x.GetString()??"").ToArray();
            // 附件目录是存储日期，不是公告公布日期；真实2024年年报路径早于公布日一天。
            var attachment=Regex.Match(path,@"^/disc/disk[0-9]{2}/finalpage/(?<stored>[0-9]{4}-[0-9]{2}-[0-9]{2})/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.PDF$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
            if(!securities.Contains(key.Ticker,StringComparer.Ordinal)||securities.Any(x=>x.Length!=6||!x.All(char.IsAsciiDigit))||securities.Distinct().Count()!=securities.Length
                ||!Text(row,"attachFormat").Equals("PDF",StringComparison.OrdinalIgnoreCase)
                ||!attachment.Success||!DateOnly.TryParseExact(attachment.Groups["stored"].Value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out _))throw new InvalidDataException("深交所公告证券、格式或路径不匹配。");
            var title=Text(row,"title");var kind=title.Contains("季度报告",StringComparison.Ordinal)?"季度报告":title.Contains("半年度报告",StringComparison.Ordinal)||title.Contains("中期报告",StringComparison.Ordinal)?"中期报告":title.Contains("年度报告",StringComparison.Ordinal)?"年度报告":throw new InvalidDataException("目录返回了定期报告之外的文档。");
            Add(result,new(id,title,kind,date,rawTime,"https://disc.static.szse.cn/download"+path,Array.AsReadOnly(securities)),from,through);
        }
        return new(total,count,result.AsReadOnly());
    }
    private static void Add(List<EtfReportIndexDocument> rows,EtfReportIndexDocument row,DateOnly from,DateOnly through)
    {
        if(row.Announced<from||row.Announced>through||rows.Any(x=>x.DocumentUrl==row.DocumentUrl||x.ReferenceId==row.ReferenceId)
            ||rows.Count>0&&row.Announced>rows[^1].Announced)throw new InvalidDataException("基金目录超期、重复或顺序不符。");
        rows.Add(row);
    }
    private static int Count(int total,int size,int page,JsonElement rows)
    {
        var pages=Math.Max(1,(total+size-1)/size);
        if(total is <0 or >MaximumDocuments||page>pages||rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()!=Math.Min(size,Math.Max(0,total-(page-1)*size)))throw new InvalidDataException("基金目录分页缺失、越界或超限。");
        return pages;
    }
    public static IReadOnlyList<EtfReportIndexPage> ParseCapture(EtfKey key,DateOnly from,DateOnly through,IReadOnlyList<byte[]> pages,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(pages.Count is <1 or >40||pages.Any(x=>x is null)||pages.Sum(x=>(long)x.Length)>MaximumCaptureBytes)throw new InvalidDataException("ETF目录响应缺失或超过整次查询限额。");
        var parsed=new List<EtfReportIndexPage>();
        for(var i=0;i<pages.Count;i++)parsed.Add(Parse(key,from,through,i+1,pages[i],token));
        var first=parsed[0];var rows=parsed.SelectMany(x=>x.Documents).ToArray();
        if(parsed.Count!=first.Pages||parsed.Any(x=>x.Total!=first.Total||x.Pages!=first.Pages)||rows.Length!=first.Total||rows.Select(x=>x.DocumentUrl).Distinct().Count()!=rows.Length
            ||rows.Select(x=>x.ReferenceId).Distinct().Count()!=rows.Length||rows.Zip(rows.Skip(1)).Any(x=>x.First.Announced<x.Second.Announced))throw new InvalidDataException("ETF目录跨页变化、重复或不完整。");
        return parsed.AsReadOnly();
    }
    private static void Validate(EtfKey key,DateOnly from,DateOnly through,int page)
    {ValidateKey(key);if(from>through||through.DayNumber-from.DayNumber>1100||page is <1 or >40)throw new InvalidDataException("基金目录只接受1100天内区间及有界分页。");}
    internal static void ValidateKey(EtfKey key)
    {if(key.Market is not ("SSE" or "SZSE")||key.Ticker is null||key.Ticker.Length!=6||!key.Ticker.All(char.IsAsciiDigit))throw new InvalidDataException("基金目录只接受两市六位代码。");}
    private static DateOnly Date(string value)=>DateOnly.ParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture);
    private static string Text(JsonElement row,string key)
    {var value=row.GetProperty(key).GetString();return string.IsNullOrWhiteSpace(value)||value.Length>1000?throw new InvalidDataException("基金目录文字缺失或超限。"):value;}
    private static void Unique(JsonElement value,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var item in value.EnumerateObject()){if(!names.Add(item.Name))throw new InvalidDataException("基金目录JSON属性重复。");Unique(item.Value,token);}}
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Unique(item,token);
    }
}
