using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CRS.Application;
using CRS.Infrastructure;

/// <summary>授权的真实原件只读验证；输出匿名索引与摘要，不记录账户、文件名或交易金额。</summary>
internal static class LocalRealSampleChecks
{
    public static void Run(string directory,string config,string output,string workspace)
    {
        var files=Directory.GetFiles(directory).Where(p=>Path.GetExtension(p) is ".xlsx" or ".pdf").Order(StringComparer.Ordinal).ToArray();
        var before=files.Select(Hash).ToArray();
        var store=TestStores.Create(Path.Combine(workspace,"real-encrypted-store"));
        var rows=new List<object>();
        foreach(var year in Enumerable.Range(2021,5))
        {
            var activity=files.Single(p=>Path.GetFileName(p).StartsWith(year+"_",StringComparison.Ordinal));
            var income=files.Single(p=>Path.GetFileName(p).StartsWith(year.ToString(),StringComparison.Ordinal) && p!=activity);
            var calculator=CalculationServices.Create(new ExchangeRates(config),"FUTU",year);
            var timer=Stopwatch.StartNew();
            // 未获期初及账户范围确认，始终保持待复核；程序结果不能当作独立人工基准。
            var result=calculator.Calculate([income,activity],year,false,false,saveCanonicalSnapshot:true);
            store.Save(result); var loaded=store.Load(result.SnapshotId); var replay=calculator.Replay(loaded); store.Save(replay);
            if(result.Summary!=replay.Summary || !result.Matches.SequenceEqual(replay.Matches)
                || result.EstimatedTopUpCny is not null || result.Complete || !loaded.IsFrozen)
                throw new Exception("真实样本持久化、复算或未确认范围门槛不一致。");
            timer.Stop();
            rows.Add(new {year,trades=result.ImportedTradeCount,matches=result.Matches.Count,incomeRows=result.AnnualIncome.Count,
                issueCodes=result.Issues.Select(i=>i.Code).Distinct().Order().ToArray(),status=result.CalculationStatus.ToString(),
                elapsedMilliseconds=timer.ElapsedMilliseconds,persistenceAndReplayVerified=true});
        }
        if(!before.SequenceEqual(files.Select(Hash))) throw new Exception("验收期间原件发生变化。");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"local-real-samples.json"),JsonSerializer.Serialize(new
        {sourceKind="real-private",broker="FUTU",originalsUnchanged=true,originals=before.Select((sha256,index)=>new {index,sha256}),
            rows,independentAmountsApproved=false,releaseApproved=false},new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine("FUTU 五年度真实原件读取、SQLCipher 持久化及复算一致性通过；原件未改写。独立金额签认仍待补充。");
    }
    private static string Hash(string path) {using var stream=File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream));}
}
