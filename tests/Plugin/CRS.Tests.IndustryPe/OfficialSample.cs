using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Application;
using CRS.Plugins.NationalTeamWatch.Domain;
using CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>读取已取得的官网原件做隔离复算，不冒充本次正式服务联网或历史当时已知。</summary>
internal static class OfficialSample
{
    public static void Run(string source,string output)
    {
        if(Directory.Exists(output))throw new IOException("原件复算输出已存在，拒绝覆盖。");Directory.CreateDirectory(output);
        var bytes=File.ReadAllBytes(source);var date=new DateOnly(2026,10,23);var now=DateTimeOffset.UtcNow;
        var document=new CapturedIndustryPeDocument(OfficialIndustryPeSource.Source(date).AbsoluteUri,date,bytes,now,Convert.ToHexString(SHA256.HashData(bytes)));
        var parsed=OfficialIndustryPeSource.Parse(document,now);var bank=parsed.Single(x=>x.Code=="C07");
        if(parsed.Count!=11||bank.RollingWeighted!=7.36m||bank.RollingMedian!=9.2m||bank.StaticWeighted!=7.99m)throw new InvalidDataException("10月23日官方黄金原件字段核对失败。");
        var key=RandomNumberGenerator.GetBytes(32);var raw=new RawEvidenceStore(Path.Combine(output,"raw"));var data=Path.Combine(output,"data");IndustryPeImportOutcome outcome;IndustryPeReport report;
        using(var repository=new MarketRepository(data,raw,approvalAuditKey:key))
        {
            outcome=repository.ImportIndustryPe(document,Guid.NewGuid());var cutoff=DateTimeOffset.UtcNow;
            var revisions=((IMarketReadSessions)repository).Read(r=>r.IndustryPeRevisions(date,date,cutoff),default);
            report=IndustryPeResearch.Calculate(date,date,cutoff,revisions,new Dictionary<DateOnly,bool?>{{date,null}},new('a',64));
            if(outcome.Added!=11||revisions.Any(x=>!x.EvidenceHealthy)||report.Rows.Any(x=>x.PublishedSeriesPercentile.HasValue))throw new InvalidDataException("原件隔离登记或缺口边界失败。");
        }
        using(var reopened=new MarketRepository(data,raw,approvalAuditKey:key))if(reopened.IndustryPeRevisions(date,date,DateTimeOffset.UtcNow).Count!=11)throw new InvalidDataException("原件隔离重启回读失败。");
        var options=new JsonSerializerOptions{WriteIndented=true};File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,options));
        File.WriteAllText(Path.Combine(output,"acceptance.json"),JsonSerializer.Serialize(new{Installed=false,Transport="CapturedOfficialFileReplay",SourceUrl=document.Url,document.Sha256,ReplayReadAt=now,Outcome=outcome,ReopenedRows=11,OpportunityState=report.OpportunityState},options));
        Console.WriteLine("PASS 真实公开原件隔离复算：11行业，滚动/静态及加权/中位数核对，加密入库/重启回读；历史分位与机会均未通过。");
    }
}
