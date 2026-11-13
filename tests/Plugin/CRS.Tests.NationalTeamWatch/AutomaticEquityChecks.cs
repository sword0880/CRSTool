using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static class AutomaticEquityChecks
{
    public static void Probe(string directory)
    {
        var root=Path.GetFullPath(directory);var research=Path.Combine(Path.GetDirectoryName(root)!,"equity-remediation-20261104");
        var securities=AutomaticEquitySource.ParseSecurities(File.ReadAllBytes(Path.Combine(root,"cninfo-stocks.json")));
        var page=AutomaticEquitySource.ParsePage(new(2026,10,31),1,File.ReadAllBytes(Path.Combine(root,"global-20261031-page1.json")),securities.ToDictionary(x=>x.Ticker,StringComparer.Ordinal));
        Console.WriteLine($"CATALOG {securities.Length}沪深A股身份；正式单日目录{page.Total}条，第一页合格原文{page.Documents.Count}份");
        var sources=ReviewedEquitySources.All.Where(x=>x.ReportAsOf.Month==9).ToArray();var output=new List<object>();
        foreach(var source in sources)
        {
            var stock=securities.Single(x=>x.Ticker==source.Ticker);
            var document=new AutomaticEquityDocument(source.Id,stock.Market,stock.Ticker,stock.Name,stock.OrgId,stock.Name+"2026年第三季度报告",source.Published,source.ReportAsOf,source.Url);
            try
            {
                var proof=AutomaticEquityPdf.Parse(document,File.ReadAllBytes(Path.Combine(research,stock.Ticker,"research",stock.Ticker+"-q3-fixed.pdf")));
                output.Add(new{Ticker=stock.Ticker,State="Verified",Proof=proof});Console.WriteLine($"VERIFIED {stock.Ticker}: {proof.Accounts.Count}个机构账户；完整十行A股表");
            }
            catch(InvalidDataException error){output.Add(new{Ticker=stock.Ticker,State="Unsupported",Reason=error.Message});Console.WriteLine($"UNKNOWN {stock.Ticker}: {error.Message}");}
            if(stock.Ticker is "600426" or "600153" or "688012")
            {
                var pages=CRS.Documents.Pdf.PdfReader.ReadFirst(File.ReadAllBytes(Path.Combine(research,stock.Ticker,"research",stock.Ticker+"-q3-fixed.pdf")),10);
                File.WriteAllText(Path.Combine(root,stock.Ticker+"-layout.json"),JsonSerializer.Serialize(pages,new JsonSerializerOptions{WriteIndented=true}));
                File.WriteAllText(Path.Combine(root,stock.Ticker+"-layout.txt"),string.Join("\nPAGE\n",pages.Select(p=>CRS.Documents.Pdf.PdfTextLayout.Lines(p.Letters))));
            }
        }
        File.WriteAllText(Path.Combine(root,"parser-probe.json"),JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true}));
    }
}
