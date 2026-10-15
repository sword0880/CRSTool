using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>两市场分别采集和报告故障，一个来源异常不抹去另一个来源已经验证的数据。</summary>
public sealed class OfficialShareSources(IPluginHttpGateway http,IRawEvidenceStore raw):IOfficialShareSource
{
    public Task<SourceDay> FetchAsync(DateOnly day,CancellationToken token)=>throw new InvalidOperationException("两市采集需要明确的有效证券池。");
    public async Task<SourceDay> FetchAsync(DateOnly day,IReadOnlyList<EtfKey> pool,CancellationToken token)
    {
        var pages=new List<SourcePage>();var issues=new List<string>();
        async Task Capture(string name,Func<Task<SourceDay>> fetch)
        {
            try{pages.AddRange((await fetch()).Pages);}
            // 只有调用方取消才终止整批；单个来源的网络超时不能吞掉另一交易所的数据。
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(OperationCanceledException){issues.Add(name+"：官方来源响应超时。");}
            catch(Exception error){issues.Add(name+"："+error.Message);}
        }
        if(pool.Any(x=>x.Market=="SSE"))await Capture("SSE",()=>new SseOfficialShareSource(http,raw).FetchAsync(day,token));
        foreach(var key in pool.Where(x=>x.Market=="SZSE"))await Capture("SZSE "+key.Ticker,()=>new SzseOfficialShareSource(http,raw,key.Ticker).FetchAsync(day,token));
        if(pages.Count==0)throw new InvalidDataException(string.Join("；",issues.DefaultIfEmpty("有效池为空，没有采集来源。")));
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
        var rows=new List<ImportRow>();
        foreach(var value in table.GetProperty("data").EnumerateArray())
        {
            var date=DateOnly.ParseExact(value.GetProperty("size_date").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var code=value.GetProperty("fund_code").GetString();var number=value.GetProperty("current_size").GetString()!;
            if(date!=expected || code!=ticker || number.Length>100 || !Regex.IsMatch(number,@"^(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?$",RegexOptions.None,TimeSpan.FromMilliseconds(100)))throw new InvalidDataException("深交所日期、代码或份额格式校验失败。");
            rows.Add(new(new("SZSE",ticker),date,decimal.Parse(number,NumberStyles.AllowDecimalPoint|NumberStyles.AllowThousands,CultureInfo.InvariantCulture),"万份"));
        }
        if(rows.Count>1 || total>1 || total==0 && rows.Count>0 || total>0 && (count==0 || rows.Count==0))throw new InvalidDataException("深交所单证券单日期记录不一致。");
        return(rows,count,total);
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
