using System.Globalization;
using System.Text.Json;
using CRS.Application.Performance;
using CRS.Domain.Performance;

namespace CRS.Infrastructure;

/// <summary>只请求公开指数与汇率，不上传账户、持仓或金额；原响应与来源摘要一并存入加密库。</summary>
internal sealed class YahooPerformanceMarketData(HttpClient? suppliedClient=null) : IPerformanceMarketData
{
    public async Task<PerformanceDocument> FetchAsync(string symbol,DateOnly from,DateOnly to,string currency,CancellationToken cancellation)
    {
        var benchmark=PerformanceBenchmarks.Get(symbol);var ticker=benchmark.Ticker;
        if(from>=to || from.Year<2000 || to.Year>2100 || currency is not ("CNY" or "USD" or "HKD"))throw new CrsException("指数行情期间或报告币种无效。");
        using var owned=suppliedClient is null?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(25)}:null;
        var client=suppliedClient??owned!;
        var responses=new Dictionary<string,string>();
        async Task<(DateOnly Date,decimal Close)[]> Request(string key)
        {
            var begin=new DateTimeOffset(from.AddDays(-7).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero).ToUnixTimeSeconds();
            var end=new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero).ToUnixTimeSeconds();
            using var message=new HttpRequestMessage(HttpMethod.Get,$"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(key)}?period1={begin}&period2={end}&interval=1d");
            message.Headers.UserAgent.ParseAdd("CRS/1.0");
            using var response=await client.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,cancellation);
            if(!response.IsSuccessStatusCode)throw new CrsException($"指数行情服务暂不可用（HTTP {(int)response.StatusCode}），可稍后重试；已有资料保留。");
            if(response.Content.Headers.ContentLength>4*1024*1024)throw new CrsException("指数行情响应超过限额。");
            await using var source=await response.Content.ReadAsStreamAsync(cancellation);using var output=new MemoryStream();var buffer=new byte[16384];
            int count;while((count=await source.ReadAsync(buffer,cancellation))>0){if(output.Length+count>4*1024*1024)throw new CrsException("指数行情响应超过限额。");output.Write(buffer,0,count);}
            var json=System.Text.Encoding.UTF8.GetString(output.ToArray());responses[key]=json;
            using var parsed=JsonDocument.Parse(json);var result=parsed.RootElement.GetProperty("chart").GetProperty("result")[0];
            var zone=TimeZoneInfo.Utc;
            if(result.TryGetProperty("meta",out var meta) && meta.TryGetProperty("exchangeTimezoneName",out var zoneName))
            {if(!TimeZoneInfo.TryFindSystemTimeZoneById(zoneName.GetString()!,out var marketZone))throw new CrsException("无法识别行情市场时区，请稍后重试。");zone=marketZone;}
            var timestamps=result.GetProperty("timestamp").EnumerateArray().ToArray();var closes=result.GetProperty("indicators").GetProperty("quote")[0].GetProperty("close").EnumerateArray().ToArray();
            if(timestamps.Length!=closes.Length || timestamps.Length>40000)throw new CrsException("指数行情的日期与报价不匹配。");
            return timestamps.Select((stamp,i)=>(Date:DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(stamp.GetInt64()),zone).DateTime),Value:closes[i]))
                .Where(p=>p.Value.ValueKind==JsonValueKind.Number && p.Value.GetDecimal()>0).Select(p=>(p.Date,p.Value.GetDecimal())).Where(p=>p.Date<=to && p.Date>=from.AddDays(-7)).ToArray();
        }
        try
        {
            var quotes=await Request(ticker);var native=benchmark.Currency;
            var fx=native==currency?Array.Empty<(DateOnly Date,decimal Close)>():await Request(native+currency+"=X");
            if(quotes.Length==0)throw new CrsException("指数行情服务未返回有效历史报价。");
            var raw=JsonSerializer.SerializeToUtf8Bytes(new{Provider="Yahoo Finance",Symbol=symbol,From=from,To=to,Responses=responses});var hash=PerformanceCodec.Hash(raw);
            var data=new PerformanceData{Prices=quotes.Select(q=>new SecurityPrice("Yahoo:"+symbol+":"+q.Date,symbol,"INDEX",native,q.Date,q.Close,new(hash,ticker+":"+q.Date),PerformanceQuality.Reconstructed))
                .Concat(fx.Select(q=>new SecurityPrice("Yahoo:FX:"+symbol+":"+native+currency+":"+q.Date,native+currency,"INDEX_FX",currency,q.Date,q.Close,new(hash,native+currency+":"+q.Date),PerformanceQuality.Reconstructed))).ToArray()};
            return new(hash,"Yahoo Finance "+symbol+" "+from+" — "+to,data,raw,"YahooChartJSON");
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){throw;}
        catch(CrsException){throw;}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {throw new CrsException("指数历史行情获取失败，请检查网络或稍后重试；已有资料保留。");}
    }
}
