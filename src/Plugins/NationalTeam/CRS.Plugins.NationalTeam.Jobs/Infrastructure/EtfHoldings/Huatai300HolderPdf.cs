using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CRS.Documents.Pdf;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>仅解析已核对的两份华泰柏瑞沪深300年报模板；坐标和原件身份一起登记，未知字节不猜表。</summary>
public static class Huatai300HolderPdf
{
    public const string Version="huatai300-holders-pdf-v1";
    private sealed record Profile(string Hash,string Path,int Year,int Page,double[] Boundaries);
    private static readonly Profile[] Profiles=[
        new("9790CC85C6EE6611D52E653F4154632EFF35D588185D1269CADC77082F5FB4C4",
            "/disclosure/fund/announcement/c/new/2026-03-31/510300_20260331_W6CZ.pdf",2025,75,
            [456.55,424.75,393.07,361.37,329.57,297.89,266.21,218.93,187.22,155.54,123.74,76.46]),
        new("6EABBDBAD90860C41F87DFBE5EB2402780F3D8531A49C13EF4A20AB175820B92",
            "/disclosure/fund/announcement/c/new/2025-03-31/510300_20250331_07Z1.pdf",2024,72,
            [714.70,682.90,651.22,603.82,572.14,540.43,508.75,461.47,414.19,382.37,350.69,303.41])];

    public static EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var d=captured.Document;
        if(captured.Content.Length is <5 or >8*1024*1024||!captured.Content.AsSpan(0,5).SequenceEqual("%PDF-"u8)
            ||!Convert.ToHexString(SHA256.HashData(captured.Content)).Equals(d.Hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("持有人原件身份或容量异常。");
        var profile=Profiles.SingleOrDefault(x=>x.Hash.Equals(d.Hash,StringComparison.OrdinalIgnoreCase));
        if(profile is null)return new(d,Version,"UnsupportedTemplate","未登记该原件的持有人表模板；没有解析结果不代表主体零持仓。",null,null,null,null,null,null,[]);
        if(d.Market!="SSE"||d.Ticker!="510300"||!Uri.TryCreate(d.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"
            ||uri.Host!="www.sse.com.cn"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!=profile.Path)
            throw new InvalidDataException("持有人原件与登记证券或官方路径不匹配。");
        if(!DateTimeOffset.TryParse(d.FirstSeenAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out var firstSeen)
            ||DateOnly.FromDateTime(firstSeen.ToOffset(TimeSpan.FromHours(8)).DateTime)<new DateOnly(profile.Year+1,3,31))
            throw new InvalidDataException("持有人原件首见不能早于已登记披露日期。");
        var pages=PdfReader.ReadSelected(captured.Content,[1,profile.Page,profile.Page+1],token);
        var cover=Normalized(Text(pages.Single(x=>x.Number==1).Letters));
        if(!cover.Contains("华泰柏瑞沪深300交易型开放式指数证券投资基金",StringComparison.Ordinal)
            ||!cover.Contains(profile.Year+"年年度报告",StringComparison.Ordinal))throw new InvalidDataException("持有人报告封面身份不符。");
        var table=pages.Single(x=>x.Number==profile.Page);var text=Normalized(Text(table.Letters));
        if(!new[]{"9.2期末上市基金前十名持有人","持有人名称","持有份额（份）","占上市总份额比例（%）"}.All(text.Contains))
            throw new InvalidDataException("持有人表标题、单位或比例分母不符。");
        var footnote=Normalized(string.Join('\n',pages.Where(x=>x.Number>=profile.Page).Select(x=>Text(x.Letters))));
        if(!footnote.Contains("前十名持有人为除本基金的联接基金之外的前十名持有人",StringComparison.Ordinal))
            throw new InvalidDataException("持有人表联接基金范围脚注缺失。");
        var rows=new List<EtfHolderCandidate>();
        for(var i=0;i<profile.Boundaries.Length-1;i++)
        {
            token.ThrowIfCancellationRequested();var letters=table.Letters.Where(x=>x.BaselineY<profile.Boundaries[i]&&x.BaselineY>profile.Boundaries[i+1]).ToArray();
            var rank=Normalized(Text(letters.Where(x=>x.X is >=82 and <153)));
            var rawName=Text(letters.Where(x=>x.X is >=153 and <263.6));var name=Normalized(rawName);
            var shares=Normalized(Text(letters.Where(x=>x.X is >=263.6 and <363.5)));
            var percent=Normalized(Text(letters.Where(x=>x.X is >=363.5 and <518.7)));
            if(rank!=(i+1).ToString(CultureInfo.InvariantCulture)||name.Length is <2 or >200||rows.Any(x=>x.RegisteredHolder==name)
                ||!Regex.IsMatch(shares,@"^\d{1,3}(,\d{3})*\.\d{2}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
                ||!Regex.IsMatch(percent,@"^\d{1,3}\.\d{2}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                throw new InvalidDataException("持有人表行号、账户或数字列异常，拒绝返回半张表。");
            var q=decimal.Parse(shares,NumberStyles.AllowThousands|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
            var p=decimal.Parse(percent,CultureInfo.InvariantCulture);
            if(q<=0||p<=0||p>100)throw new InvalidDataException("持有人份额或比例范围无效。");
            rows.Add(new(i+1,name,rawName,q,p,$"PDF物理第{profile.Page}页，第9.2节，第{i+1}行",InstitutionAccounts.CandidateGroup(name)));
        }
        if(rows.Count!=11||rows.Sum(x=>x.PercentOfListedUnits)>100||!rows[^1].RegisteredHolder.Contains("联接基金",StringComparison.Ordinal))
            throw new InvalidDataException("完整持有人表或联接基金另列规则不符。");
        return new(d,Version,"NeedsReview","11条登记持有人候选；联接基金另列。需核验主体、账户、原件和独立持有关系；公告仅有日期，不补造准确时刻。",
            new(profile.Year,12,31),new(profile.Year+1,3,31),profile.Page,"份","ListedFundUnits","TopTenExcludingFeederPlusFeeder",rows);
    }
    private static string Normalized(string value)=>string.Concat(value.Where(x=>!char.IsWhiteSpace(x)));
    private static string Text(IEnumerable<PdfLetter> letters)=>PdfTextLayout.Lines(letters);
}
