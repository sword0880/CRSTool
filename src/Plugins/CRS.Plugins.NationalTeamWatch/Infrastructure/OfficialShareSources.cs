using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>两市场分别采集和报告故障，一个来源异常不抹去另一个来源已经验证的数据。</summary>
public sealed class OfficialShareSources(IPluginHttpGateway http,IRawEvidenceStore raw,Func<EtfProgress,Task>? progress=null):IOfficialShareSource
{
    private DateOnly? rangeFrom,rangeTo;
    private readonly Dictionary<string,SourceDay?> szseRanges=[];
    public Task PrepareRangeAsync(DateOnly from,DateOnly to,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();rangeFrom=from;rangeTo=to;szseRanges.Clear();return Task.CompletedTask;
    }
    private async Task<SourceDay> FetchSzseAsync(EtfKey key,DateOnly day,CancellationToken token)
    {
        var source=new SzseOfficialShareSource(http,raw,key.Ticker);
        if(rangeFrom is not {} from || rangeTo is not {} to || from==to)return await source.FetchAsync(day,token);
        if(day<from || day>to)throw new InvalidDataException("查询日期不在已声明区间内。");
        if(!szseRanges.TryGetValue(key.Ticker,out var cached))
        {
            try {szseRanges[key.Ticker]=cached=await source.FetchRangeAsync(from,to,token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception){szseRanges[key.Ticker]=null;}
        }
        if(cached is null)throw new InvalidDataException("深市历史区间采集未完成，等待重试。");
        // 原件仍为完整官方页，每日提交只选当日行；页失败或截断时整只区间不进入缓存。
        return new(day,cached.Pages.Select(p=>p with{Rows=p.Rows.Where(r=>r.Date==day).ToArray()}).ToArray());
    }
    public Task<SourceDay> FetchAsync(DateOnly day,CancellationToken token)=>throw new InvalidOperationException("两市采集需要明确的有效证券池。");
    public async Task<SourceDay> FetchAsync(DateOnly day,IReadOnlyList<EtfKey> pool,CancellationToken token)
    {
        var pages=new List<SourcePage>();var issues=new List<string>();
        async Task Capture(string name,Func<Task<SourceDay>> fetch)
        {
            var market=name.StartsWith("SSE",StringComparison.Ordinal)?"SSE":"SZSE";
            if(progress is not null)await progress(new("SourceStarted",day,market));
            SourceDay? result=null;
            try{result=await fetch();pages.AddRange(result.Pages);}
            // 只有调用方取消才终止整批；单个来源的网络超时不能吞掉另一交易所的数据。
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(OperationCanceledException){issues.Add(name+"：官方来源响应超时。");}
            catch(Exception error){issues.Add(name+"："+error.Message);}
            // 日志只保留阶段与公开数量，不持久化来源异常消息。
            if(progress is not null)await progress(new(result is null?"SourceFailed":"SourceParsed",day,market,Received:result?.Pages.Sum(x=>x.Rows.Count)??0));
        }
        if(pool.Any(x=>x.Market=="SSE"))await Capture("SSE",()=>new SseOfficialShareSource(http,raw).FetchAsync(day,token));
        foreach(var key in pool.Where(x=>x.Market=="SZSE"))await Capture("SZSE "+key.Ticker,()=>FetchSzseAsync(key,day,token));
        if(pages.Count==0 && issues.Count==0)throw new InvalidDataException("有效池为空，没有采集来源。");
        return new(day,pages,issues);
    }
}
/// <summary>使用深交所官方列表内规模链接的历史报表，严格核对日期和万份表头。</summary>
public sealed class SzseOfficialShareSource(IPluginHttpGateway http,IRawEvidenceStore? raw,string ticker):IOfficialShareSource
{
    public static Uri Request(string ticker,DateOnly day,int page)
    {
        if(ticker.Length!=6 || !ticker.All(char.IsAsciiDigit))throw new InvalidDataException("深交所代码无效。");
        return new($"https://www.szse.cn/api/report/ShowReport/data?SHOWTYPE=JSON&CATALOGID=fund_jjgm&TABKEY=tab1&txtDm={ticker}&txtStart={day:yyyy-MM-dd}&txtEnd={day:yyyy-MM-dd}&PAGENO={page}");
    }
    public static (List<ImportRow> Rows,int PageCount,int Total) Parse(byte[] bytes,string ticker,DateOnly expected,int page)
        =>ParseRange(bytes,ticker,expected,expected,page);
    public static (List<ImportRow> Rows,int PageCount,int Total) ParseRange(byte[] bytes,string ticker,DateOnly from,DateOnly to,int page)
    {
        using var document=JsonDocument.Parse(bytes);
        var table=document.RootElement.EnumerateArray().Single(x=>x.GetProperty("metadata").GetProperty("catalogid").GetString()=="fund_jjgm");
        if(table.TryGetProperty("error",out var error) && error.ValueKind!=JsonValueKind.Null)throw new InvalidDataException("深交所报表返回错误。");
        var metadata=table.GetProperty("metadata");
        var unit=metadata.GetProperty("cols").GetProperty("current_size").GetString()!;
        var header=Regex.Replace(unit,"<[^>]*>","",RegexOptions.None,TimeSpan.FromMilliseconds(100)).Replace("（","(").Replace("）",")").Replace(" ","");
        if(header!="基金规模(万份)")throw new InvalidDataException("深交所份额列单位发生变化，停止解析。");
        static int Number(JsonElement item)=>int.Parse(item.ToString(),CultureInfo.InvariantCulture);
        var total=Number(metadata.GetProperty("recordcount"));var count=Number(metadata.GetProperty("pagecount"));
        if(Number(metadata.GetProperty("pageno"))!=page || total<0 || total>1000 || count<0 || count>50)throw new InvalidDataException("深交所分页参数无效。");
        var rows=new List<ImportRow>();var dates=new HashSet<DateOnly>();
        foreach(var value in table.GetProperty("data").EnumerateArray())
        {
            var date=DateOnly.ParseExact(value.GetProperty("size_date").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var code=value.GetProperty("fund_code").GetString();var number=value.GetProperty("current_size").GetString()!;
            if(date<from || date>to || !dates.Add(date) || code!=ticker || number.Length>100 || !Regex.IsMatch(number,@"^(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?$",RegexOptions.None,TimeSpan.FromMilliseconds(100)))throw new InvalidDataException("深交所日期、代码或份额格式校验失败。");
            rows.Add(new(new("SZSE",ticker),date,decimal.Parse(number,NumberStyles.AllowDecimalPoint|NumberStyles.AllowThousands,CultureInfo.InvariantCulture),"万份"));
        }
        if(from==to && (rows.Count>1 || total>1) || total==0 && rows.Count>0 || total>0 && (count==0 || rows.Count==0) || rows.Count>total)throw new InvalidDataException("深交所记录与分页计数不一致。");
        return(rows,count,total);
    }
    public async Task<SourceDay> FetchRangeAsync(DateOnly from,DateOnly to,CancellationToken token)
    {
        if(from>to || to.DayNumber-from.DayNumber>400)throw new InvalidDataException("历史区间超限。");
        var pages=new List<SourcePage>();var dates=new HashSet<DateOnly>();int count=1;int? total=null;
        for(var page=1;page<=count;page++)
        {
            var uri=new Uri(Request(ticker,from,page).AbsoluteUri.Replace($"txtEnd={from:yyyy-MM-dd}",$"txtEnd={to:yyyy-MM-dd}",StringComparison.Ordinal));
            var response=await http.GetAsync(uri,token);token.ThrowIfCancellationRequested();raw?.Save(response.Content);
            if(response.StatusCode!=200)throw new InvalidDataException($"深市历史份额请求HTTP {response.StatusCode}。");
            var parsed=ParseRange(response.Content,ticker,from,to,page);
            if(total is not null && (total!=parsed.Total || count!=parsed.PageCount))throw new InvalidDataException("深市历史分页口径改变。");
            total=parsed.Total;count=parsed.PageCount;
            if(parsed.Rows.Any(row=>!dates.Add(row.Date)))throw new InvalidDataException("深市历史跨页重复。");
            pages.Add(new(uri,response.Content,parsed.Rows,"SZSE"));
        }
        if(dates.Count!=(total??0))throw new InvalidDataException("深市历史分页截断。");
        return new(from,pages);
    }
    public async Task<SourceDay> FetchAsync(DateOnly day,CancellationToken token)
    {
        var uri=Request(ticker,day,1);var response=await http.GetAsync(uri,token);token.ThrowIfCancellationRequested();raw?.Save(response.Content);
        if(response.StatusCode!=200)throw new InvalidDataException($"官方份额请求HTTP {response.StatusCode}，没有生成虚假数据。");
        var result=Parse(response.Content,ticker,day,1);
        if(result.PageCount>1 || result.Rows.Count!=result.Total)throw new InvalidDataException("深交所报表分页截断。");
        return new(day,[new(uri,response.Content,result.Rows,"SZSE")]);
    }
}
