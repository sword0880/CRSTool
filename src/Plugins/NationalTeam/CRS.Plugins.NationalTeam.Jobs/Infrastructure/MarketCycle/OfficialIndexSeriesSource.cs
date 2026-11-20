using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>官网显式脚本的有限公开地址；价格与当前估值快照分开解析，不猜测估值历史日期参数。</summary>
public static class OfficialIndexSeriesSource
{
    public const string ParserVersion="cnindex-index-series-v1";
    public static readonly Uri UnitsSource=new("https://www.cnindex.com.cn/module/index-detail.html?act_menu=1&indexCode=399317");
    public static readonly Uri DaySource=new("https://www.cnindex.com.cn/index/queryDay");
    public static readonly Uri ValuationSource=new("https://www.cnindex.com.cn/index/indexList?channelCode=201&rows=20&pageNum=1");
    private static readonly string[] Columns=["timestamp","current","high","open","low","close","chg","percent","amount","volume","avg"];
    public static Uri PriceSource(string code,DateOnly from,DateOnly through)
    {
        _=IndexResearchCatalog.Get(code);
        if(from>through||through.DayNumber-from.DayNumber>400||from<new DateOnly(2003,1,1))throw new InvalidDataException("指数来源范围须为2003年后且不超过400个自然日。");
        return new("https://hq.cnindex.com.cn/market/market/getIndexDailyDataWithDataFormat?indexCode="+code+"&startDate="+Day(from)+"&endDate="+Day(through)+"&frequency=day");
    }
    public static bool IsSource(Uri uri)
    {
        if(uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)return false;
        if(uri==UnitsSource||uri==DaySource||uri==ValuationSource||IndexResearchCatalog.Definitions.Values.Any(x=>uri.AbsoluteUri==x.MethodUrl))return true;
        var match=Regex.Match(uri.AbsoluteUri,@"^https://hq\.cnindex\.com\.cn/market/market/getIndexDailyDataWithDataFormat\?indexCode=([0-9]{6})&startDate=([0-9-]{10})&endDate=([0-9-]{10})&frequency=day$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1));
        if(!match.Success||!IndexResearchCatalog.Definitions.ContainsKey(match.Groups[1].Value)||!DateOnly.TryParseExact(match.Groups[2].Value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||!DateOnly.TryParseExact(match.Groups[3].Value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var through))return false;
        try{return uri==PriceSource(match.Groups[1].Value,from,through);}catch(InvalidDataException){return false;}
    }
    public static async Task<CapturedIndexDocument> FetchAsync(IPluginHttpGateway gateway,string role,Uri uri,CancellationToken token)
    {
        if(!IsSource(uri))throw new InvalidDataException("指数来源未核对。");var response=await gateway.GetAsync(uri,token);
        if(response.StatusCode!=200)throw new HttpRequestException("指数官网未返回公开原件。",null,(System.Net.HttpStatusCode)response.StatusCode);
        var bytes=response.Content.ToArray();return new(role,uri.AbsoluteUri,bytes,DateTimeOffset.UtcNow,Convert.ToHexString(SHA256.HashData(bytes)));
    }
    public static IReadOnlyList<IndexSeriesValue> Parse(CapturedIndexPacket packet,DateTimeOffset now)
    {
        try{return ParseStrict(packet,now);}
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {throw new InvalidDataException("指数原件结构、日期、单位或字段类型变化，拒绝部分解析。",error);}
    }
    private static IReadOnlyList<IndexSeriesValue> ParseStrict(CapturedIndexPacket packet,DateTimeOffset now)
    {
        if(packet.Kind is not ("Price" or "Valuation")||packet.From>packet.Through||packet.Through.DayNumber-packet.From.DayNumber>400||packet.Through>DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime))throw new InvalidDataException("指数原件请求身份无效。");
        if(packet.Documents.Count!=(packet.Kind=="Price"?3:4)||packet.Documents.Select(x=>x.Role).Distinct().Count()!=packet.Documents.Count)throw new InvalidDataException("指数证明文件缺失或角色重复。");
        foreach(var doc in packet.Documents)
            if(doc.RetrievedAt==default||doc.RetrievedAt>now||doc.Content.Length is 0 or >8388608||!Uri.TryCreate(doc.Url,UriKind.Absolute,out var uri)||!IsSource(uri)||Convert.ToHexString(SHA256.HashData(doc.Content))!=doc.Sha256)throw new InvalidDataException("指数原件取得时间、地址或哈希无效。");
        var definition=packet.Kind=="Price"?IndexResearchCatalog.Get(packet.Code??""):IndexResearchCatalog.Get("399381");
        var method=Document(packet,"Method");if(method.Url!=definition.MethodUrl||method.Sha256!=definition.MethodHash||!method.Content.AsSpan().StartsWith("%PDF-"u8))throw new InvalidDataException("指数方法版本改变或原件不符，须重新核对目录。");
        return packet.Kind=="Price"?Prices(packet,definition):Valuations(packet);
    }
    private static IReadOnlyList<IndexSeriesValue> Prices(CapturedIndexPacket packet,IndexResearchDefinition definition)
    {
        var units=Document(packet,"Units");var html=Encoding.UTF8.GetString(units.Content);
        if(units.Url!=UnitsSource.AbsoluteUri||!html.Contains("成交量单位为万手",StringComparison.Ordinal)||!html.Contains("成交额单位为亿元",StringComparison.Ordinal)||!html.Contains("指数样本数据的汇总结果",StringComparison.Ordinal))throw new InvalidDataException("指数成交单位或样本汇总口径未确认。");
        var data=Document(packet,"Price");if(data.Url!=PriceSource(definition.Code,packet.From,packet.Through).AbsoluteUri)throw new InvalidDataException("指数日线原件与请求不匹配。");
        using var json=ParseJson(data);var root=json.RootElement;Success(root);var body=root.GetProperty("data");
        if(body.GetProperty("indexCode").GetString()!=definition.Code||Name(body.GetProperty("indexName").GetString())!=definition.Name||!body.GetProperty("item").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(Columns))throw new InvalidDataException("指数代码、名称或字段头变化。");
        var array=body.GetProperty("data");if(array.ValueKind!=JsonValueKind.Array||root.GetProperty("total").GetInt32()!=array.GetArrayLength()||array.GetArrayLength()>401)throw new InvalidDataException("指数日线数量截断或异常。");
        var result=new List<IndexSeriesValue>();var dates=new HashSet<DateOnly>();DateOnly? previous=null;
        foreach(var row in array.EnumerateArray())
        {
            if(row.ValueKind!=JsonValueKind.Array||row.GetArrayLength()!=Columns.Length)throw new InvalidDataException("指数日线行字段数变化。");
            var date=DateOnly.ParseExact(row[0].GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(date<packet.From||date>packet.Through||date>DateOnly.FromDateTime(data.RetrievedAt.ToOffset(TimeSpan.FromHours(8)).DateTime)||!dates.Add(date)||previous is {} prior&&date>=prior)throw new InvalidDataException("指数日线日期越界、重复或未按官网倒序。");previous=date;
            var close=Number(row[5],false)!.Value;var high=Number(row[2],false)!.Value;var open=Number(row[3],false)!.Value;var low=Number(row[4],false)!.Value;
            if(close<=0||low<=0||low>open||open>high||low>close||close>high||Number(row[1],false)!=close)throw new InvalidDataException("指数日线OHLC或收盘字段冲突。");
            var amount=Number(row[8],true);var volume=Number(row[9],true);if(amount<0||volume<0)throw new InvalidDataException("指数样本成交不能为负。");
            result.Add(new(definition.Code,date,"Price",close,open,high,low,amount*100000000m,volume*1000000m));
        }
        return result.OrderBy(x=>x.Date).ToArray();
    }
    private static IReadOnlyList<IndexSeriesValue> Valuations(CapturedIndexPacket packet)
    {
        if(packet.Code is not null||packet.From!=packet.Through)throw new InvalidDataException("当前估值只允许固定行业目录的同日快照。");
        var before=Document(packet,"HeaderBefore");var after=Document(packet,"HeaderAfter");var source=Document(packet,"Valuation");
        if(source.Url!=ValuationSource.AbsoluteUri||before.RetrievedAt>source.RetrievedAt||source.RetrievedAt>after.RetrievedAt||HeaderDate(before)!=packet.From||HeaderDate(after)!=packet.From)throw new InvalidDataException("估值前后统计日期不同、取得顺序异常或不能归属所选日。");
        using var json=ParseJson(source);var root=json.RootElement;Success(root);var body=root.GetProperty("data");var array=body.GetProperty("rows");
        if(body.GetProperty("total").GetInt32() is <20 or >800||array.ValueKind!=JsonValueKind.Array||array.GetArrayLength()!=20)throw new InvalidDataException("估值目录分页结构变化。");
        var rows=array.EnumerateArray().ToArray();if(rows.Select(x=>x.GetProperty("indexcode").GetString()).Distinct().Count()!=rows.Length)throw new InvalidDataException("估值目录代码重复。");
        var values=new List<IndexSeriesValue>();
        foreach(var definition in IndexResearchCatalog.Definitions.Values.Where(x=>x.Family=="Cni1000Sector"))
        {
            var matches=rows.Where(x=>x.GetProperty("indexcode").GetString()==definition.Code).ToArray();
            if(matches.Length!=1)throw new InvalidDataException("固定行业目录未完整出现在已核对列表页，不猜测新分页。");var row=matches[0];
            if(Name(row.GetProperty("indexname").GetString())!=definition.Name||row.GetProperty("indextype").GetString()!="201")throw new InvalidDataException("估值指数名称或行业类别不符。");
            var close=Number(row.GetProperty("closeingPoint"),false)!.Value;var count=row.GetProperty("samplesize");
            int? samples=count.ValueKind==JsonValueKind.Null?null:count.GetInt32();if(close<=0||samples<0)throw new InvalidDataException("估值点位或列示样本数异常。");
            // 非正估值保留原始事实供审计；展示/分位另判不适用。列示样本数0不代表零成分或估值覆盖100%。
            values.Add(new(definition.Code,packet.From,"Valuation",close,PublishedStaticPe:Number(row.GetProperty("peStatic"),true),PublishedRollingPe:Number(row.GetProperty("peDynamic"),true),PublishedPb:Number(row.GetProperty("pb"),true),ReportedSamples:samples));
        }
        return values;
    }
    public static DateOnly HeaderDate(CapturedIndexDocument document)
    {
        if(document.Url!=DaySource.AbsoluteUri)throw new InvalidDataException("估值统计日来源不符。");using var json=ParseJson(document);Success(json.RootElement);
        var date=DateTimeOffset.FromUnixTimeMilliseconds(json.RootElement.GetProperty("data").GetInt64()).ToOffset(TimeSpan.FromHours(8));
        if(date.TimeOfDay!=TimeSpan.Zero||date>DateTimeOffset.MaxValue||date>document.RetrievedAt)throw new InvalidDataException("统计日时间戳无效或在原件取得之后。");return DateOnly.FromDateTime(date.DateTime);
    }
    private static CapturedIndexDocument Document(CapturedIndexPacket packet,string role)=>packet.Documents.SingleOrDefault(x=>x.Role==role)??throw new InvalidDataException("指数原件证明角色缺失。");
    private static JsonDocument ParseJson(CapturedIndexDocument document){var json=JsonDocument.Parse(document.Content,new(){MaxDepth=24});try{RejectDuplicates(json.RootElement);return json;}catch{json.Dispose();throw;}}
    private static void RejectDuplicates(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException("指数JSON包含重复属性。");RejectDuplicates(p.Value);}}
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())RejectDuplicates(item);
    }
    private static void Success(JsonElement root){if(root.ValueKind!=JsonValueKind.Object||root.GetProperty("code").GetInt32()!=200)throw new InvalidDataException("指数官网未确认查询成功。");}
    private static decimal? Number(JsonElement value,bool nullable)
    {
        if(value.ValueKind==JsonValueKind.Null&&nullable)return null;
        if(value.ValueKind!=JsonValueKind.Number||!value.TryGetDecimal(out var number)||Math.Abs(number)>100000000000000m)throw new InvalidDataException("指数数值类型或范围异常。");return number;
    }
    private static string Name(string? value)=>(value??"").Replace(" ","",StringComparison.Ordinal);
    private static string Day(DateOnly day)=>day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
}
