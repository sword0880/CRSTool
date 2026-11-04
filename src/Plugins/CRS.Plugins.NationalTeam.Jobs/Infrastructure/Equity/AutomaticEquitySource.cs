using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>巨潮正式披露目录：固定表单、闭合北京时间单日及有界分页；不猜测PDF地址或下载其它附件。</summary>
public static class AutomaticEquitySource
{
    public static readonly Uri SecuritiesUri=new("https://www.cninfo.com.cn/new/data/szse_stock.json");
    public static readonly Uri ReportsUri=new("https://www.cninfo.com.cn/new/hisAnnouncement/query");
    public const int PageSize=30;
    public const string Categories="category_ndbg_szsh;category_bndbg_szsh;category_yjdbg_szsh;category_sjdbg_szsh";
    public sealed record Page(int Total,IReadOnlyList<AutomaticEquityDocument> Documents,int OriginalRows,IReadOnlyList<string> Ids);
    public static byte[] Request(DateOnly date,int page)=>JsonSerializer.SerializeToUtf8Bytes(new SortedDictionary<string,string>(StringComparer.Ordinal)
    {
        ["pageNum"]=page.ToString(CultureInfo.InvariantCulture),["pageSize"]="30",["column"]="szse",["tabName"]="fulltext",
        ["seDate"]=$"{date:yyyy-MM-dd}~{date:yyyy-MM-dd}",["category"]=Categories,["sortName"]="time",["sortType"]="desc"
    });
    public static IReadOnlyDictionary<string,string> ValidatePost(Uri uri,byte[] body)
    {
        if(uri!=ReportsUri||body.Length is 0 or >4096)throw new InvalidDataException("自动披露查询地址或大小不符。");
        var fields=JsonSerializer.Deserialize<SortedDictionary<string,string>>(body)??throw new InvalidDataException("披露查询为空。");
        if(!fields.TryGetValue("seDate",out var range)||range.Length!=21||range[10]!='~'
            ||!DateOnly.TryParseExact(range[..10],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
            ||range[11..]!=range[..10]||!fields.TryGetValue("pageNum",out var text)||!int.TryParse(text,out var page)||page is <1 or >1000
            ||!body.AsSpan().SequenceEqual(Request(date,page)))throw new InvalidDataException("披露查询必须使用固定字段、闭合单日及有界分页。");
        return fields;
    }
    public static bool IsPdf(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Host=="static.cninfo.com.cn"
        &&uri.Query.Length==0&&uri.Fragment.Length==0&&Regex.IsMatch(uri.AbsolutePath,@"^/finalpage/20\d{2}-\d{2}-\d{2}/\d{8,15}\.PDF$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static bool IsSource(Uri uri)=>uri==SecuritiesUri||uri==ReportsUri||IsPdf(uri);
    public static AutomaticEquitySecurity[] ParseSecurities(byte[] bytes)
    {
        using var json=JsonDocument.Parse(bytes);var list=json.RootElement.GetProperty("stockList");
        if(list.ValueKind!=JsonValueKind.Array||list.GetArrayLength() is <100 or >10000)throw new InvalidDataException("官方证券目录数量无效。");
        var result=new Dictionary<string,AutomaticEquitySecurity>(StringComparer.Ordinal);
        foreach(var row in list.EnumerateArray())
        {
            var code=row.GetProperty("code").GetString()!;var category=row.GetProperty("category").GetString();
            if(category!="A股")continue;
            var market=code.StartsWith('6')?"SSE":code.StartsWith('0')||code.StartsWith('3')?"SZSE":null;
            if(market is null)continue;
            var name=row.GetProperty("zwjc").GetString()!;var org=row.GetProperty("orgId").GetString()!;
            if(code.Length!=6||!code.All(char.IsAsciiDigit)||string.IsNullOrWhiteSpace(name)||name.Length>100
                ||!Regex.IsMatch(org,@"^[a-zA-Z0-9]{1,50}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
                ||!result.TryAdd(code,new(market,code,name,org)))throw new InvalidDataException("证券身份缺失或重复。");
        }
        if(result.Count<100)throw new InvalidDataException("官方A股目录不完整。");
        return result.Values.OrderBy(x=>x.Ticker,StringComparer.Ordinal).ToArray();
    }
    public static Page ParsePage(DateOnly date,int page,byte[] bytes,IReadOnlyDictionary<string,AutomaticEquitySecurity> securities)
    {
        using var json=JsonDocument.Parse(bytes);var root=json.RootElement;var total=root.GetProperty("totalAnnouncement").GetInt32();
        var rows=root.GetProperty("announcements");var count=rows.ValueKind==JsonValueKind.Null?0:rows.GetArrayLength();
        if(total is <0 or >30000||page is <1 or >1000||count!=Math.Min(PageSize,Math.Max(0,total-(page-1)*PageSize))
            ||root.GetProperty("hasMore").GetBoolean()!=(page*PageSize<total))throw new InvalidDataException("披露目录分页截断或不一致。");
        var result=new List<AutomaticEquityDocument>();var ids=new HashSet<string>(StringComparer.Ordinal);
        if(count==0)return new(total,result,0,[]);
        foreach(var row in rows.EnumerateArray())
        {
            var code=row.GetProperty("secCode").GetString()!;var id=row.GetProperty("announcementId").GetString()!;
            var published=DateOnly.FromDateTime(CRS.Timing.BeijingClock.Normalize(DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("announcementTime").GetInt64())).DateTime);
            if(published!=date||!ids.Add(id))throw new InvalidDataException("目录公告日期或身份重复。");
            if(!securities.TryGetValue(code,out var security)||code=="603195")continue;
            if(row.GetProperty("orgId").GetString()!=security.OrgId)throw new InvalidDataException("公告披露主体与官方证券目录不符。");
            var title=Regex.Replace(row.GetProperty("announcementTitle").GetString()!,@"\s+","");
            var period=Regex.Match(title,@"(?<year>20\d{2})年(?<type>年度|半年度|第一季度|第三季度)报告(?:全文|（(?:修订|更新|更正)[^（）]{0,20}）)?$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            if(!period.Success)continue;
            var year=int.Parse(period.Groups["year"].Value,CultureInfo.InvariantCulture);
            var asOf=period.Groups["type"].Value switch{"年度"=>new DateOnly(year,12,31),"半年度"=>new DateOnly(year,6,30),"第一季度"=>new DateOnly(year,3,31),_=>new DateOnly(year,9,30)};
            var url=new Uri(new Uri("https://static.cninfo.com.cn/"),row.GetProperty("adjunctUrl").GetString()).AbsoluteUri;
            if(!IsPdf(new(url))||url!=$"https://static.cninfo.com.cn/finalpage/{published:yyyy-MM-dd}/{id}.PDF"
                ||row.GetProperty("adjunctType").GetString()!="PDF"||asOf>published)throw new InvalidDataException("报告原文路径、股类证券身份或截止日不符。");
            var size=row.GetProperty("adjunctSize").GetInt32();
            if(size is <0 or >1000000)throw new InvalidDataException("报告附件大小字段异常。");
            result.Add(new(id,security.Market,code,security.Name,security.OrgId,title,published,asOf,url,size));
        }
        return new(total,result,count,ids.ToArray());
    }
}
