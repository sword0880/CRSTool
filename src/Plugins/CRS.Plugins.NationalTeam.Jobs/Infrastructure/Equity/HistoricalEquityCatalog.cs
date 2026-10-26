using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>固定原件的人工核对摘录适配器，不宣称通用PDF解析；金融值、范围和原文身份一起版本化。</summary>
internal static class HistoricalEquityCatalog
{
    internal sealed record ReviewedAccount(string Account,string Institution,string Page);
    internal sealed record Definition(string Id,string Market,string Ticker,string Name,DateOnly ReportAsOf,DateOnly Published,string PublicationBasis,
        string PublicationUrl,string DocumentUrl,string OriginalHash,string Pages,string ShareClass,string PercentBasis,string Unit,
        string Scope,decimal TotalShares,EquityHolder[] Holders,ReviewedAccount[] ReviewedAccounts);
    private static readonly Lazy<(Definition Value,byte[] Bytes,string Hash)> Loaded=new(()=>
    {
        using var stream=typeof(HistoricalEquityCatalog).Assembly.GetManifestResourceStream("CRS.Plugins.NationalTeam.Jobs.Resources.cnd-2025-annual.json")
            ??throw new InvalidDataException("历史原文摘录资源缺失。");
        using var memory=new MemoryStream();stream.CopyTo(memory);var bytes=memory.ToArray();
        var value=JsonSerializer.Deserialize<Definition>(bytes,new JsonSerializerOptions{RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true})
            ??throw new InvalidDataException("历史原文摘录无效。");
        var uri=new Uri(value.DocumentUrl);
        if(value.Market!="SSE"||value.Ticker!="600153"||value.ShareClass!="A"||value.PercentBasis!="TotalShares"||value.Unit!="股"
            ||value.Scope!="Top10ShareholdersExcludingSecuritiesLending"||value.ReportAsOf>value.Published||value.TotalShares<=0
            ||!OfficialEquityDocuments.IsReviewedIssuer(uri)||!OfficialEquityDocuments.Matches(uri,value.Market,value.Ticker,value.ReportAsOf)
            ||!string.Equals(OfficialEquityDocuments.ReviewedIssuerHash(uri),value.OriginalHash,StringComparison.OrdinalIgnoreCase)
            ||value.Holders.Length!=10||value.Holders.Select(x=>x.Account).Distinct().Count()!=10||value.Holders.Sum(x=>x.Shares)>value.TotalShares
            ||value.Holders.Any(x=>string.IsNullOrWhiteSpace(x.Account)||x.Shares<0||x.Shares!=decimal.Truncate(x.Shares)||x.Percent is not {} percent
                ||decimal.Round(x.Shares/value.TotalShares*100,2,MidpointRounding.AwayFromZero)!=percent)
            ||value.ReviewedAccounts.Length!=4||value.ReviewedAccounts.Select(x=>x.Account).Distinct().Count()!=4
            ||value.ReviewedAccounts.Any(x=>!value.Holders.Any(h=>h.Account==x.Account)||InstitutionAccounts.CandidateGroup(x.Account) is null
                ||x.Institution is not ("社保基金" or "养老保险基金")||InstitutionAccounts.Group(x.Account) is {} group&&group!=x.Institution
                ||string.IsNullOrWhiteSpace(x.Page)||x.Page.Length>100))
            throw new InvalidDataException("历史原文摘录与固定来源、完整表、单位或逐账户身份不一致。");
        // 摘录身份按结构化内容规范化，不让源码换行风格改变同一已核数据的身份。
        var normalized=JsonSerializer.SerializeToUtf8Bytes(value);
        return(value,normalized,Convert.ToHexString(SHA256.HashData(normalized)));
    });
    public static IReadOnlyList<HistoricalEquityPdf> Documents()=>[Summary(Loaded.Value)];
    private static HistoricalEquityPdf Summary((Definition Value,byte[] Bytes,string Hash) item)
    {var v=item.Value;return new(v.Id,v.Market,v.Ticker,v.Name,v.ReportAsOf,v.Published,v.PublicationBasis,v.DocumentUrl,v.OriginalHash,item.Hash,v.Pages,v.Holders.Length,v.ReviewedAccounts.Length,v.Scope);}
    public static (Definition Value,byte[] Bytes,string Hash) Get(string id)=>id==Loaded.Value.Value.Id?Loaded.Value:throw new InvalidDataException("历史原文定义未登记，不能导入任意PDF。");
}
