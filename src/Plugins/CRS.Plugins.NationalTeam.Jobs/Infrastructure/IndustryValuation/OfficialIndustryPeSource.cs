using System.Globalization;
using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>URL模板和字段来自官网页面及其显式引用脚本，限定国证分类、深沪京市场、一级行业。</summary>
public static class OfficialIndustryPeSource
{
    public const string ParserVersion="cnindex-published-pe-v1";
    // 来源路径固定公历与不变文化，不能随桌面地区设置改变日期或请求身份。
    public static Uri Source(DateOnly date)=>new("https://www.cnindex.com.cn/syl/"+date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"/cninfo.json");
    public static bool IsSource(Uri uri)
    {
        var parts=uri.AbsolutePath.Split('/');
        return uri.Scheme=="https"&&uri.Host=="www.cnindex.com.cn"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Query.Length==0&&uri.Fragment.Length==0
            &&parts.Length==4&&parts[1]=="syl"&&parts[3]=="cninfo.json"&&DateOnly.TryParseExact(parts[2],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
            &&date>=new DateOnly(2021,4,1)&&uri.AbsoluteUri==Source(date).AbsoluteUri;
    }
    public static async Task<CapturedIndustryPeDocument> FetchAsync(IPluginHttpGateway http,DateOnly date,CancellationToken token)
    {
        var uri=Source(date);var response=await http.GetAsync(uri,token);
        if(response.StatusCode!=200)throw new InvalidDataException("行业PE官网未返回已公布文件。");
        var now=DateTimeOffset.UtcNow;var bytes=response.Content.ToArray();
        return new(uri.AbsoluteUri,date,bytes,now,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    }
    public static IReadOnlyList<IndustryPeValues> Parse(CapturedIndustryPeDocument document,DateTimeOffset now)
    {
        try{return ParseStrict(document,now);}
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {throw new InvalidDataException("行业PE官网原件结构或字段类型变化，拒绝部分解析。",error);}
    }
    private static IReadOnlyList<IndustryPeValues> ParseStrict(CapturedIndustryPeDocument document,DateTimeOffset now)
    {
        if(!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)||!IsSource(uri)||uri!=Source(document.Date)||document.RetrievedAt==default||document.RetrievedAt>now
            ||document.Date>DateOnly.FromDateTime(document.RetrievedAt.ToOffset(TimeSpan.FromHours(8)).DateTime)||document.Content.Length is 0 or >8388608
            ||Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(document.Content))!=document.Sha256)throw new InvalidDataException("行业PE原件地址、日期、取得时间或哈希无效。");
        using var parsed=JsonDocument.Parse(document.Content,new(){MaxDepth=16});var root=parsed.RootElement;RejectDuplicates(root);
        if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength() is <1 or >20)throw new InvalidDataException("行业PE市场组结构变化。");
        var groups=root.EnumerateArray().ToArray();
        if(groups.Select(x=>x.GetProperty("id").GetInt32()).Distinct().Count()!=groups.Length)throw new InvalidDataException("行业PE市场组重复。");
        var candidates=groups.Where(x=>x.GetProperty("id").GetInt32()==4).ToArray();
        if(candidates.Length!=1||Text(candidates[0],"plate")!="深沪京市场")throw new InvalidDataException("行业PE目标市场身份不符。");
        var items=candidates[0].GetProperty("sylItemVoList");
        if(items.ValueKind!=JsonValueKind.Array||items.GetArrayLength() is <11 or >500)throw new InvalidDataException("行业PE项目数量异常。");
        var values=new List<IndustryPeValues>();
        foreach(var item in items.EnumerateArray())
        {
            if(Text(item,"tl")!="1")continue;
            var code=Text(item,"tc");var name=Text(item,"tn");
            if(!IndustryPeResearch.Sectors.TryGetValue(code,out var expected)||expected!=name||Text(item,"tt")!="008200"||Text(item,"plate")!="深沪京市场"
                ||Text(item,"date")!=document.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)
                ||!int.TryParse(Text(item,"ca"),NumberStyles.None,CultureInfo.InvariantCulture,out var count)||count is <0 or >10000)throw new InvalidDataException("行业PE分类、日期或公司数量不符。");
            // d=滚动、s=静态、w=加权、m=中位数，官网表头逐一核对，不能互换或填NA为零。
            var row=new IndustryPeValues(code,name,count,Number(item,"dwer"),Number(item,"dmer"),Number(item,"swer"),Number(item,"smer"));
            if(count==0&&new[]{row.RollingWeighted,row.RollingMedian,row.StaticWeighted,row.StaticMedian}.Any(x=>x.HasValue))throw new InvalidDataException("零公司行业不能出现有效PE。");
            values.Add(row);
        }
        if(values.Count!=11||values.Select(x=>x.Code).Distinct().Count()!=11)throw new InvalidDataException("行业PE一级行业缺失或重复，拒绝部分入库。");
        return values.OrderBy(x=>x.Code,StringComparer.Ordinal).ToArray();
    }
    private static string Text(JsonElement element,string key)=>element.GetProperty(key).GetString()??throw new InvalidDataException("行业PE字段为空。");
    private static decimal? Number(JsonElement element,string key)
    {
        var text=Text(element,key);if(text=="NA")return null;
        if(!decimal.TryParse(text,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||value is <=0 or >1000000)throw new InvalidDataException("行业PE数值异常，不能补零或转换为低估。");
        return value;
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in element.EnumerateObject()){if(!keys.Add(property.Name))throw new InvalidDataException("行业原件JSON重复字段。");RejectDuplicates(property.Value);}
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var value in element.EnumerateArray())RejectDuplicates(value);
    }
}
