using System.Security.Cryptography;
using System.Text.Json;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

internal static partial class HistoricalEquityCatalog
{
    internal sealed record SubsetReview(Definition Definition,DateOnly? CandidateSourceDate,string? CandidateSourceHash,EquityHolder[] CandidateHolders,string Resolution);
    private sealed record PreparedSubset(SubsetReview Review,(Definition Value,byte[] Bytes,string Hash) Prepared);
    private static readonly Lazy<IReadOnlyDictionary<string,PreparedSubset>> ReviewedSubsets=new(LoadReviewedSubsets);
    internal static SubsetReview? CandidateResolution(string id)=>ReviewedSubsets.Value.TryGetValue(id,out var value)
        &&value.Review.CandidateSourceDate is not null?value.Review:null;
    internal static void ValidateSubsetReview(EquityReview review,EquityHolder holder)
    {
        if(ReviewedEquitySources.Find(new Uri(review.DocumentUrl)) is not {} source)return;
        var definition=Get(source.Id).Value;
        var expected=definition.Holders.SingleOrDefault(x=>x.Account==review.Account);
        var account=definition.ReviewedAccounts.SingleOrDefault(x=>x.Account==review.Account);
        if(expected is null||account is null||holder.Shares!=expected.Shares||holder.Percent!=expected.Percent
            ||review.Institution!=account.Institution||review.Nature!="Unknown"||review.Comparable)
            throw new InvalidDataException("固定A股子集不能批准H股数量、未核账户、投资目的或跨期增减。");
    }
    private static IReadOnlyDictionary<string,PreparedSubset> LoadReviewedSubsets()
    {
        using var stream=typeof(HistoricalEquityCatalog).Assembly.GetManifestResourceStream("CRS.NationalTeam.ReviewedEquitySubsets.json")
            ??throw new InvalidDataException("已核A股披露子集资源缺失。");
        var reviews=JsonSerializer.Deserialize<SubsetReview[]>(stream,new JsonSerializerOptions{RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true})
            ??throw new InvalidDataException("已核A股披露子集资源无效。");
        var result=new Dictionary<string,PreparedSubset>(StringComparer.Ordinal);
        foreach(var review in reviews)
        {
            ValidateReviewedSubset(review);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(review);
            var prepared=(review.Definition,bytes,Convert.ToHexString(SHA256.HashData(bytes)));
            if(!result.TryAdd(review.Definition.Id,new(review,prepared)))throw new InvalidDataException("已核披露子集身份重复。");
        }
        return result;
    }
    private static void ValidateReviewedSubset(SubsetReview review)
    {
        var value=review.Definition;
        var source=ReviewedEquitySources.Find(new Uri(value.DocumentUrl));
        if(source is null||source.Id!=value.Id||source.Market!=value.Market||source.Ticker!=value.Ticker||source.ReportAsOf!=value.ReportAsOf
            ||source.Published!=value.Published||source.Sha256!=value.OriginalHash||value.Scope!="ReviewedAShareDisclosedSubset"
            ||value.ShareClass!="A"||value.PercentBasis!="TotalShares"||value.Unit!="股")
            throw new InvalidDataException("A股子集与精确原文身份或股类口径不符。");
        if(value.TotalShares is <=0||value.TotalShares is {} total&&value.Holders.Sum(x=>x.Shares)>total
            ||value.Holders.Length>10||value.Holders.Select(x=>x.Account).Distinct().Count()!=value.Holders.Length
            ||value.ReviewedAccounts.Length!=value.Holders.Length||value.ReviewedAccounts.Select(x=>x.Account).Distinct().Count()!=value.Holders.Length
            ||value.Holders.Any(x=>!ValidReviewedHolder(x,value.TotalShares))||value.ReviewedAccounts.Any(x=>!ValidReviewedAccount(x,value))
            ||string.IsNullOrWhiteSpace(value.Pages)||value.Pages.Length>100||string.IsNullOrWhiteSpace(review.Resolution))
            throw new InvalidDataException("A股子集账户、股数、披露比例或原文位置无效。");
        if(review.CandidateHolders.Length>10||review.CandidateHolders.Select(x=>x.Account).Distinct().Count()!=review.CandidateHolders.Length
            ||review.CandidateSourceDate is null&&(review.CandidateHolders.Length!=0||review.CandidateSourceHash is not null)
            ||review.CandidateSourceDate is not null&&(review.CandidateSourceHash?.Length!=64||!review.CandidateSourceHash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("原候选对照身份不完整。");
    }
    private static bool ValidReviewedHolder(EquityHolder holder,decimal? total)=>!string.IsNullOrWhiteSpace(holder.Account)
        &&holder.Shares>0&&holder.Shares==decimal.Truncate(holder.Shares)&&holder.Percent is not (<0 or >100)
        &&(holder.Percent is not {} percent||total is not {} shares||decimal.Round(holder.Shares/shares*100,2,MidpointRounding.AwayFromZero)==percent);
    private static bool ValidReviewedAccount(ReviewedAccount account,Definition definition)=>definition.Holders.Any(x=>x.Account==account.Account)
        &&InstitutionAccounts.CandidateGroup(account.Account) is not null&&InstitutionAccounts.IsSupportedGroup(account.Institution)
        &&(InstitutionAccounts.Group(account.Account) is not {} known||known==account.Institution)
        &&!string.IsNullOrWhiteSpace(account.Page)&&account.Page.Length<=100;
}
