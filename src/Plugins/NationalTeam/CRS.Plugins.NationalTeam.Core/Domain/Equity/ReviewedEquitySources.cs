using System.Text.Json;
using System.Text.RegularExpressions;

namespace CRS.Plugins.NationalTeamWatch.Domain;

public sealed record ReviewedEquitySource(string Id,string Market,string Ticker,DateOnly ReportAsOf,DateOnly Published,string Url,string Sha256);

/// <summary>只声明经正式披露目录定位的固定原件身份；来源入册不等于批准机构持仓。</summary>
public static class ReviewedEquitySources
{
    private static readonly Lazy<IReadOnlyList<ReviewedEquitySource>> Loaded=new(Load);
    public static IReadOnlyList<ReviewedEquitySource> All=>Loaded.Value;
    public static ReviewedEquitySource? Find(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0
        &&uri.Host=="static.cninfo.com.cn"&&uri.Query.Length==0&&uri.Fragment.Length==0
        ?All.SingleOrDefault(x=>x.Url==uri.AbsoluteUri):null;
    private static IReadOnlyList<ReviewedEquitySource> Load()
    {
        using var stream=typeof(ReviewedEquitySources).Assembly.GetManifestResourceStream("CRS.NationalTeam.ReviewedEquitySources.json")
            ??throw new InvalidDataException("固定披露原文来源资源缺失。");
        var rows=JsonSerializer.Deserialize<ReviewedEquitySource[]>(stream,new JsonSerializerOptions{RespectRequiredConstructorParameters=true,RespectNullableAnnotations=true})
            ??throw new InvalidDataException("固定披露原文来源为空。");
        if(rows.Length is 0 or >100||rows.Select(x=>x.Id).Distinct().Count()!=rows.Length||rows.Select(x=>x.Url).Distinct().Count()!=rows.Length)
            throw new InvalidDataException("固定原文身份重复或数量无效。");
        foreach(var row in rows)
        {
            if(string.IsNullOrWhiteSpace(row.Id)||row.Market is not ("SSE" or "SZSE")||row.Ticker.Length!=6||!row.Ticker.All(char.IsAsciiDigit)
                ||row.ReportAsOf>row.Published||row.Sha256.Length!=64||!row.Sha256.All(Uri.IsHexDigit)
                ||!Uri.TryCreate(row.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0
                ||uri.Host!="static.cninfo.com.cn"||uri.Query.Length!=0||uri.Fragment.Length!=0
                ||!Regex.IsMatch(uri.AbsolutePath,@"^/finalpage/\d{4}-\d{2}-\d{2}/\d+\.PDF$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                throw new InvalidDataException("固定原文必须绑定证券、报告期、精确披露路径及SHA256。");
        }
        return Array.AsReadOnly(rows);
    }
}
