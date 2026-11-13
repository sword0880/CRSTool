using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>正式服务原件抽查：独立读取官方字段，不调用生产解析器，也不自动批准异常可比性。</summary>
internal static class P0OfficialSpotChecks
{
    public static async Task Run()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var token=deadline.Token;var reader=new WorkerEtfWorkspace();
        var directory=Path.Combine(Environment.CurrentDirectory,"artifacts","p0-live","spot-checks");Directory.CreateDirectory(directory);
        var results=new List<object>();
        foreach(var key in new[]{new EtfKey("SSE","510300"),new EtfKey("SZSE","159919"),new EtfKey("SSE","510310")})
        {
            var evidence=await reader.EvidenceAsync(key,token);
            var accepted=evidence.Where(x=>x.State=="Accepted").OrderBy(x=>x.TradeDate).DistinctBy(x=>x.TradeDate).ToArray();
            if(accepted.Length<10)throw new InvalidDataException("有效交易日不足10日，不能通过抽查。");
            // 在实际覆盖范围均匀选10日，同时保留这只证券全部待审日期，不能绕开异常抽样。
            var samples=Enumerable.Range(0,10).Select(i=>accepted[i*(accepted.Length-1)/9]).Concat(evidence.Where(x=>x.State=="NeedsReview")).DistinctBy(x=>x.RevisionId);
            foreach(var sample in samples)
            {
                var file=Path.Combine(directory,sample.OriginalHash+".json");
                if(!File.Exists(file))await reader.ExportEvidenceAsync(sample.RevisionId,true,file,token);
                var bytes=await File.ReadAllBytesAsync(file,token);
                if(!Convert.ToHexString(SHA256.HashData(bytes)).Equals(sample.OriginalHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("抽查原件哈希不符。");
                using var json=JsonDocument.Parse(bytes);decimal original;
                if(key.Market=="SSE")
                {
                    var row=json.RootElement.GetProperty("result").EnumerateArray().Single(x=>x.GetProperty("SEC_CODE").GetString()==key.Ticker&&x.GetProperty("STAT_DATE").GetString()==sample.TradeDate);
                    original=decimal.Parse(row.GetProperty("TOT_VOL").ToString(),CultureInfo.InvariantCulture);
                }
                else
                {
                    var table=json.RootElement[0];
                    if(!table.GetProperty("metadata").GetProperty("cols").GetProperty("current_size").ToString().Contains("万份"))throw new InvalidDataException("官方单位未确认。");
                    var row=table.GetProperty("data").EnumerateArray().Single(x=>x.GetProperty("fund_code").GetString()==key.Ticker&&x.GetProperty("size_date").GetString()==sample.TradeDate);
                    original=decimal.Parse(row.GetProperty("current_size").ToString(),NumberStyles.Number,CultureInfo.InvariantCulture);
                }
                var actual=decimal.Parse(sample.Shares,CultureInfo.InvariantCulture);
                if(original*10000m!=actual)throw new InvalidDataException("原件万份与事实股数不一致。");
                results.Add(new{key.Market,key.Ticker,sample.TradeDate,sample.State,OriginalWanShares=original,StoredShares=actual,sample.OriginalHash,sample.SourceUrl,ValuesMatched=true,ComparabilityApproved=false});
                Console.WriteLine($"PASS 官方原件抽查 {key.Market}:{key.Ticker} {sample.TradeDate} {sample.State}：万份换算与存证事实一致；未代替异常可比性审核。");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory,"results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}),token);
    }
}
