using System.Text.Json;
using CRS.Plugin.Abstractions;
using CRS.Plugins.NationalTeamWatch.Infrastructure;
using CRS.Plugins.NationalTeamWatch.Domain;
internal static class HistoryRangeChecks
{
    public static async Task Run(string root)
    {
        var from=new DateOnly(2026,9,1);var to=from.AddDays(2);
        byte[] Page(int page,string[] dates,int total=3,int count=2)=>JsonSerializer.SerializeToUtf8Bytes(new[]{new{metadata=new{catalogid="fund_jjgm",cols=new{current_size="基金规模<br>(万份)"},recordcount=total,pagecount=count,pageno=page},data=dates.Select(d=>new{size_date=d,fund_code="159919",current_size="100"}).ToArray(),error=(string?)null}});
        var first=Page(1,["2026-09-01","2026-09-02"]);var second=Page(2,["2026-09-03"]);
        var http=new RangeHttp(first,second);var raw=new RawEvidenceStore(Path.Combine(root,"range-raw"));
        var sources=new OfficialShareSources(http,raw);
        await sources.PrepareRangeAsync(from,to,default);
        var values=new List<decimal>();
        for(var date=from;date<=to;date=date.AddDays(1))
        {
            var day=await sources.FetchAsync(date,[new EtfKey("SZSE","159919")],default);
            var rows=day.Pages.SelectMany(x=>x.Rows).ToArray();
            if(rows.Length!=1 || rows[0].Date!=date)throw new Exception("区间缓存未正确切分到每日。");values.Add(rows[0].Original);
        }
        if(http.Requests!=2 || values.Count!=3)throw new Exception("深市区间重复请求或分页不完整。");
        await sources.PrepareRangeAsync(from,to,default);await sources.FetchAsync(from,[new EtfKey("SZSE","159919")],default);
        if(http.Requests!=4)throw new Exception("新采集没有清空旧缓存。");
        async Task Reject(byte[] a,byte[] b)
        {
            try{await new SzseOfficialShareSource(new RangeHttp(a,b),raw,"159919").FetchRangeAsync(from,to,default);}
            catch(System.IO.InvalidDataException){return;}throw new Exception("异常区间被接受。");
        }
        await Reject(first,Page(2,["2026-09-02"]));
        await Reject(first,Page(2,["2026-09-03"],4));
        await Reject(first,Page(2,[]));
        await Reject(first,Page(2,["2026-09-04"]));
        var brokenHttp=new RangeHttp(first,Page(2,[]));var broken=new OfficialShareSources(brokenHttp,raw);
        await broken.PrepareRangeAsync(from,to,default);
        for(var i=0;i<2;i++)try{await broken.FetchAsync(from.AddDays(i),[new EtfKey("SZSE","159919")],default);}catch(System.IO.InvalidDataException){ }
        if(brokenHttp.Requests!=2)throw new Exception("失败区间在同轮被逐日重复请求。");
        Console.WriteLine("PASS 深市历史区间：完整分页、逐日切分、范围/重复/截断拒绝、新轮缓存清理及失败不重复请求。");
    }
    private sealed class RangeHttp(byte[] first,byte[] second):IPluginHttpGateway
    {
        public int Requests {get;private set;}
        public Task<PublicHttpResponse> GetAsync(Uri uri,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Requests++;
            if(!uri.Query.Contains("txtStart=2026-09-01") || !uri.Query.Contains("txtEnd=2026-09-03"))throw new Exception("历史请求区间错误。");
            return Task.FromResult(new PublicHttpResponse(200,uri.Query.Contains("PAGENO=2")?second:first,"application/json"));
        }
    }
}
