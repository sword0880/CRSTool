using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>官网公司页公告目录；目录和标题只用于找原文，不批准股东、报告期或持仓。</summary>
public static class OfficialEquityReports
{
    public const int PageSize=10;
    public static Uri Request(EtfKey key,DateOnly from,DateOnly to,int page)
    {
        if(key.Market!="SSE"||key.Ticker.Length!=6||!key.Ticker.All(char.IsAsciiDigit)||from>to||to.DayNumber-from.DayNumber>400||page is <1 or >40)throw new InvalidDataException("当前报告检索仅支持沪市、400天内区间及有界分页。");
        return new($"https://query.sse.com.cn/security/stock/queryCompanyBulletinNew.do?isPagination=true&pageHelp.pageSize={PageSize}&pageHelp.pageNo={page}&pageHelp.beginPage={page}&pageHelp.endPage={page}&pageHelp.cacheSize=1&START_DATE={from:yyyy-MM-dd}&END_DATE={to:yyyy-MM-dd}&SECURITY_CODE={key.Ticker}&TITLE=%E6%8A%A5%E5%91%8A&BULLETIN_TYPE=");
    }
    public static EquityReportPage Parse(EtfKey key,DateOnly from,DateOnly to,int page,byte[] bytes)
    {
        _=Request(key,from,to,page);using var json=JsonDocument.Parse(bytes);var root=json.RootElement;
        if(root.GetProperty("SECURITY_CODE").GetString()!=key.Ticker||root.GetProperty("START_DATE").GetString()!=from.ToString("yyyy-MM-dd")||root.GetProperty("END_DATE").GetString()!=to.ToString("yyyy-MM-dd")||root.GetProperty("TITLE").GetString()!="报告")throw new InvalidDataException("公告查询回显代码、区间或关键词不符。");
        var help=root.GetProperty("pageHelp");var total=help.GetProperty("total").GetInt32();var count=help.GetProperty("pageCount").GetInt32();var groups=root.GetProperty("result");
        if(total is <0 or >400||help.GetProperty("pageNo").GetInt32()!=page||help.GetProperty("pageSize").GetInt32()!=PageSize||count!=(int)Math.Ceiling(total/(double)PageSize)||page>Math.Max(1,count)||groups.ValueKind!=JsonValueKind.Array||groups.GetArrayLength()!=Math.Min(PageSize,Math.Max(0,total-(page-1)*PageSize)))throw new InvalidDataException("公告目录分页截断、越界或数量不符。");
        var documents=new List<EquityReportDocument>();var groupIds=new List<string>();
        foreach(var group in groups.EnumerateArray())
        {
            if(group.ValueKind!=JsonValueKind.Array||group.GetArrayLength() is <1 or >30)throw new InvalidDataException("公告附件组数量异常。");
            string? groupId=null;var urls=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in group.EnumerateArray())
            {
                var id=row.GetProperty("ORG_BULLETIN_ID").GetString();var title=row.GetProperty("TITLE").GetString();var url=row.GetProperty("URL").GetString();
                var date=DateOnly.ParseExact(row.GetProperty("SSEDATE").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture);
                if(string.IsNullOrWhiteSpace(id)||id.Length>100||string.IsNullOrWhiteSpace(title)||title.Length>500||row.GetProperty("SECURITY_CODE").GetString()!=key.Ticker||date<from||date>to||groupId is not null&&groupId!=id||url is null||!urls.Add(url))throw new InvalidDataException("公告代码、日期、标识或附件重复。");
                groupId=id;
                var pattern=$@"^/disclosure/listedinfo/announcement/c/new/{date:yyyy-MM-dd}/{key.Ticker}_{date:yyyyMMdd}_[A-Za-z0-9]+\.pdf$";
                if(!Regex.IsMatch(url,pattern,RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new InvalidDataException("公告PDF路径与股票、日期不匹配。");
                var type=row.GetProperty("BULLETIN_TYPE_DESC").GetString();
                if(type is "年报" or "半年报" or "一季报" or "三季报" && !title.Contains("摘要",StringComparison.Ordinal))
                    documents.Add(new("",key.Market,key.Ticker,id,title,date,new Uri(new Uri("https://static.sse.com.cn"),url).AbsoluteUri,"Indexed",Request(key,from,to,page).AbsoluteUri));
            }
            if(groupIds.Contains(groupId!,StringComparer.Ordinal))throw new InvalidDataException("公告组重复。");groupIds.Add(groupId!);
        }
        return new(total,Math.Max(1,count),groupIds,documents);
    }
}
