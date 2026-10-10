using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml;
using CRS.Infrastructure;

internal static class ReleaseBenchmark
{
    /// <summary>只使用合成全年样本测量导入、FIFO、对账和快照总耗时，不能代替真实券商验收。</summary>
    public static void Run(string output, string workspace, ExchangeRates rates)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        foreach (var count in new[] { 100, 10000, 100000 })
        {
            // 每个文件最多一万笔并使用独立合成账户，遵守现有 25 MB 原件上限。
            var files = Enumerable.Range(0, Math.Max(1,count/10000)).Select(part =>
            {
                var path=Path.Combine(workspace,$"benchmark-{count}-{part}.xml");
                Generate(path,Math.Min(count,10000),"BENCHMARK"+part); return path;
            }).ToArray();
            var samples = new List<double>(); long allocated=0;
            for (var repeat=0; repeat<3; repeat++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var start = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                var result = CalculationServices.Create(rates).Calculate(files,2025,true,true);
                watch.Stop(); allocated=Math.Max(allocated,GC.GetTotalAllocatedBytes(true)-start);
                if (result.Matches.Count != count/2 || result.Summary.GainCny != count/2*10m
                    || result.Summary.GainTax != count || !result.Complete)
                    throw new Exception("性能样本的金额或对账不满足独立期望："+string.Join(",",result.Issues.Select(i=>i.Code)));
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }
            // 小样本用于预热，不写成性能结论；峰值工作集是进程累计值，不能冒充单阶段占用。
            if (count==100) continue;
            using var process=Process.GetCurrentProcess(); process.Refresh();
            rows.Add(new { trades=count, samplesMs=samples, medianMs=samples.Order().ElementAt(1),
                maxAllocatedBytes=allocated, processCumulativePeakWorkingSetBytes=process.PeakWorkingSet64,
                sampleFileBytes=files.Sum(path=>new FileInfo(path).Length), files=files.Length, expectedGainCny=count/2*10m });
            Console.WriteLine($"合成性能样本 {count:N0} 笔：中位耗时 {samples.Order().ElementAt(1):N0} ms，最大托管分配 {allocated/1024d/1024d:N1} MB。");
        }
        File.WriteAllText(Path.Combine(output,"benchmark.json"),JsonSerializer.Serialize(new
        {
            schema="CRS.ReleaseBenchmark.v1", sampleKind="synthetic", createdUtc=DateTimeOffset.UtcNow,
            runtime=RuntimeInformation.FrameworkDescription, os=RuntimeInformation.OSDescription,
            architecture=RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors=Environment.ProcessorCount,
            measuredScope="XML parse + FIFO + reconciliation + reference snapshot; no SQLite, export or WPF; 3 runs after warmup",
            thresholds="not agreed; measurement only", rows
        },new JsonSerializerOptions { WriteIndented=true }));
    }

    /// <summary>生成闭合买卖、零年末持仓和独立现金期末值，记录跨 100 个证券。</summary>
    private static void Generate(string path,int count,string account)
    {
        using var writer=XmlWriter.Create(path,new XmlWriterSettings { Indent=false });
        writer.WriteStartElement("FlexQueryResponse"); writer.WriteAttributeString("type","AF"); writer.WriteStartElement("FlexStatements");
        writer.WriteStartElement("FlexStatement"); Attr("accountId",account); Attr("fromDate","20250101"); Attr("toDate","20251231");
        writer.WriteStartElement("Trades");
        for (var i=0;i<count;i++)
        {
            var buy=i%2==0; var gross=buy?100:110; var time=new DateTime(2025,1,2,0,0,0).AddSeconds(i);
            writer.WriteStartElement("Trade");
            Attr("accountId",account); Attr("assetCategory","STK"); Attr("conid",(i/2%100+1).ToString()); Attr("symbol","DEMO"+(i/2%100));
            Attr("currency","USD"); Attr("tradeID",i.ToString()); Attr("levelOfDetail","EXECUTION"); Attr("dateTime",time.ToString("yyyyMMdd;HHmmss"));
            Attr("tradeDate",time.ToString("yyyyMMdd")); Attr("buySell",buy?"BUY":"SELL"); Attr("quantity",buy?"1":"-1");
            Attr("tradePrice",gross.ToString()); Attr("ibCommission","0"); Attr("ibCommissionCurrency","USD"); Attr("taxes","0"); Attr("multiplier","1");
            Attr("proceeds",(buy?-gross:gross).ToString()); Attr("netCash",(buy?-gross:gross).ToString());
            Attr("openCloseIndicator",buy?"O":"C"); Attr("transactionType","ExchTrade"); Attr("fifoPnlRealized",buy?"0":"10");
            writer.WriteEndElement();
        }
        writer.WriteEndElement(); writer.WriteStartElement("CashTransactions"); writer.WriteEndElement(); writer.WriteStartElement("OpenPositions"); writer.WriteEndElement();
        writer.WriteStartElement("CashReport"); writer.WriteStartElement("CashReportCurrency"); Attr("currency","USD"); Attr("levelOfDetail","Currency");
        Attr("fromDate","20250101"); Attr("toDate","20251231"); Attr("startingCash","0"); Attr("endingCash",(count/2*10m).ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement();
        void Attr(string name,string value)=>writer.WriteAttributeString(name,value);
    }
}
