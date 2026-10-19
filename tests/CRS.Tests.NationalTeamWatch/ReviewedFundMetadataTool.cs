using System.Text.Json;
using System.Text.Json.Serialization;
using CRS.Plugins.NationalTeamWatch;
using CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>仅应用已逐项核验的原件清单；服务重新取得的原件必须与本地核验哈希相同，否则停止。</summary>
internal static class ReviewedFundMetadataTool
{
    internal sealed record Review(string Market,string Ticker,string Url,string SHA256,string FundId,string ManagerId,string IndexId,
        DateOnly From,DateOnly Through,string Reviewer,string EvidencePage,bool Reviewed,
        [property:JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal ReportEndShares,
        [property:JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] decimal ReportStartShares);
    public static async Task Apply(string path)
    {
        var rows=JsonSerializer.Deserialize<Review[]>(await File.ReadAllTextAsync(path))??throw new InvalidDataException("核验清单为空。");
        if(rows.Length is <1 or >100||rows.Select(x=>(x.Market,x.Ticker)).Distinct().Count()!=rows.Length
            ||rows.Any(x=>!x.Reviewed||x.ReportEndShares<=0||x.ReportStartShares<=0||string.IsNullOrWhiteSpace(x.EvidencePage)||x.SHA256.Length!=64||!x.SHA256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("只接受明确核验、有证据位置和原件哈希的逐证券清单。");
        var workspace=new WorkerEtfWorkspace();using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(30));var token=deadline.Token;
        if((await workspace.AlertChannelsAsync(token)).Any(x=>x.Enabled&&x.Configured))
            throw new InvalidDataException("存在已启用外发渠道；本次仅核验原件，不自动触发真实通知。");
        var root=Path.Combine(Environment.CurrentDirectory,"artifacts","monitor-readiness","metadata-application");Directory.CreateDirectory(root);
        foreach(var row in rows)
        {
            var key=new EtfKey(row.Market,row.Ticker);var recordPath=Path.Combine(root,row.Market+"-"+row.Ticker+".json");
            // 部分报告包含场外份额：只有同日报告总份额与交易所实际事实一致，才可登记可比口径。
            var points=await MonitorReadinessTool.Read(()=>workspace.PointsAsync(key,token),token);
            var endPoint=points.SingleOrDefault(x=>x.Date==row.Through);
            var startPoint=points.SingleOrDefault(x=>x.Date==row.From.AddDays(-1));
            // 份额来源以万份两位小数展示；保留原数，仅在核对展示精度时进行舍入。
            var rounded=decimal.Round(row.ReportEndShares/10000m,2,MidpointRounding.AwayFromZero)*10000m;
            var roundedStart=decimal.Round(row.ReportStartShares/10000m,2,MidpointRounding.AwayFromZero)*10000m;
            if(endPoint is null||startPoint is null||endPoint.Shares!=row.ReportEndShares&&endPoint.Shares!=rounded
                ||startPoint.Shares!=row.ReportStartShares&&startPoint.Shares!=roundedStart)
            {
                await File.WriteAllTextAsync(recordPath,JsonSerializer.Serialize(new{CheckedAt=DateTimeOffset.UtcNow,Status=endPoint is null||startPoint is null?"AwaitingSameDateFact":"ShareScopeMismatch",
                    Review=row,ExchangeFact=endPoint,ExchangeBaseline=startPoint,ReportEndShares=row.ReportEndShares,RoundedForWanSharesDisplay=rounded},new JsonSerializerOptions{WriteIndented=true}),token);
                Console.WriteLine($"PENDING 份额口径 {row.Market}:{row.Ticker}：{(endPoint is null||startPoint is null?"尚缺期初或期末同日已接受事实":"报告总份额与交易所事实不一致")}，未审批。");continue;
            }
            // 原件已登记时不重复下载；同URL被替换的内容不能继承本次审核。
            var documents=await workspace.AlertDocumentsAsync(key,token);
            var document=documents.SingleOrDefault(x=>x.Url==row.Url&&x.Hash.Equals(row.SHA256,StringComparison.OrdinalIgnoreCase));
            if(document is null)
            {
                await workspace.RegisterAlertDocumentAsync(new(row.Market,row.Ticker,row.Url),token);
                documents=await workspace.AlertDocumentsAsync(key,token);
                document=documents.SingleOrDefault(x=>x.Url==row.Url&&x.Hash.Equals(row.SHA256,StringComparison.OrdinalIgnoreCase))
                    ??throw new InvalidDataException("服务取得原件与核验哈希不一致，停止审批："+row.Ticker);
            }
            var existing=(await workspace.AlertMetadataAsync(token)).SingleOrDefault(x=>x.Market==row.Market&&x.Ticker==row.Ticker);
            if(existing is null||existing.EvidenceHash!=document.Hash||existing.FundId!=row.FundId||existing.ManagerId!=row.ManagerId
                ||existing.IndexId!=row.IndexId||existing.ComparableFrom!=row.From||existing.ComparableThrough!=row.Through||!existing.OriginalConfirmed)
                await workspace.ReviewAlertMetadataAsync(new(document.Id,row.FundId,row.ManagerId,row.IndexId,row.From,row.Through,row.Reviewer,true),token);
            var applied=(await workspace.AlertMetadataAsync(token)).Single(x=>x.Market==row.Market&&x.Ticker==row.Ticker);
            if(!applied.OriginalConfirmed||!applied.Covers(row.From,row.Through)||applied.EvidenceHash!=document.Hash)throw new InvalidDataException("核验后回读不符："+row.Ticker);
            await File.WriteAllTextAsync(recordPath,JsonSerializer.Serialize(new{AppliedAt=DateTimeOffset.UtcNow,Status="Applied",Review=row,ExchangeFact=endPoint,ExchangeBaseline=startPoint,Document=document,Metadata=applied},new JsonSerializerOptions{WriteIndented=true}),token);
            Console.WriteLine($"PASS 原件口径 {row.Market}:{row.Ticker} {row.From:yyyy-MM-dd}至{row.Through:yyyy-MM-dd}；SHA256一致，未扩大到报告期之后。");
        }
    }
}
