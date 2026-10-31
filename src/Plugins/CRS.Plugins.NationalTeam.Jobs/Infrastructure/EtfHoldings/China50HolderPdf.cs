using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CRS.Documents.Pdf;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>华夏上证50已核原件：年报完整读取续表，中报仅认定未披露具名表；解析不能批准持仓。</summary>
public static class China50HolderPdf
{
    public const string Version=EtfHolderParserVersions.China50;
    public const string Annual2024Url="https://www.sse.com.cn/disclosure/fund/announcement/c/new/2025-03-31/510050_20250331_INT5.pdf";
    public const string Annual2025Url="https://www.sse.com.cn/disclosure/fund/announcement/c/new/2026-03-31/510050_20260331_O8IB.pdf";
    public const string Interim2026Url="https://www.sse.com.cn/disclosure/fund/announcement/c/new/2026-08-31/510050_20260831_HP51.pdf";
    private sealed record Part(int Page,int FirstRank,double[] Bounds);
    private sealed record Profile(string Hash,string Url,DateOnly ReportAsOf,DateOnly Publication,string CoverKind,int HeaderPage,Part[] Parts);
    private static readonly Profile[] Profiles=[
        new("E7AB0DA569D9A836D753E71BED70CCA8DE5EE862EED747F09E1C939AD41F2391",Annual2024Url,new(2024,12,31),new(2025,3,31),"2024年年度报告",55,
            [new(55,1,[224,203,187,171,155,139,107,91,59]),new(56,9,[771,731,683,636])]),
        new("90E9C61673AD9C158F7A61BA36B5A14FADF5F81D702697462ADB842524B99864",Annual2025Url,new(2025,12,31),new(2026,3,31),"2025年年度报告",53,
            [new(53,1,[610,590,574,558,542,526,510,478,462,446,430,382])]),
        new("48471DBBE86A59D5DEA17F195F0CF59E49AB4A2F9BAECAA0CF6D00AD8D72A974",Interim2026Url,new(2026,6,30),new(2026,8,31),"2026年中期报告",48,[])];

    public static EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var document=captured.Document;
        if(captured.Content.Length is <5 or >8*1024*1024||!captured.Content.AsSpan(0,5).SequenceEqual("%PDF-"u8)
            ||!Convert.ToHexString(SHA256.HashData(captured.Content)).Equals(document.Hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("持有人原件身份或容量异常。");
        var profile=Profiles.SingleOrDefault(x=>x.Hash.Equals(document.Hash,StringComparison.OrdinalIgnoreCase));
        if(profile is null)return new(document,Version,"UnsupportedTemplate","未登记该原件的510050持有人版式；不将不支持解释为未披露或零持仓。",null,null,null,null,null,null,[]);
        if(document.Market!="SSE"||document.Ticker!="510050"||!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)
            ||uri.AbsoluteUri!=profile.Url||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)
            throw new InvalidDataException("持有人原件与登记证券或官方路径不匹配。");
        if(!DateTimeOffset.TryParse(document.FirstSeenAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out var firstSeen)
            ||DateOnly.FromDateTime(firstSeen.ToOffset(TimeSpan.FromHours(8)).DateTime)<profile.Publication)
            throw new InvalidDataException("持有人原件首见不能早于已登记公告日期。");
        var selected=new[]{1,profile.HeaderPage}.Concat(profile.Parts.Select(x=>x.Page))
            .Concat(profile.Parts.Length==0?[profile.HeaderPage+1]:Array.Empty<int>()).Distinct().ToArray();
        var pages=PdfReader.ReadSelected(captured.Content,selected,token);
        string Text(int page)=>Compact(PdfTextLayout.Lines(pages.Single(x=>x.Number==page).Letters,token));
        var cover=Text(1);
        if(!cover.Contains("上证50交易型开放式指数证券投资基金",StringComparison.Ordinal)
            ||!cover.Contains("基金管理人：华夏基金管理有限公司",StringComparison.Ordinal)||!cover.Contains(profile.CoverKind,StringComparison.Ordinal))
            throw new InvalidDataException("持有人报告封面身份不符。");
        var header=Text(profile.HeaderPage);
        if(profile.Parts.Length==0)
        {
            var next=Text(profile.HeaderPage+1);
            if(!new[]{"8.1期末基金份额持有人户数及持有人结构","机构投资者","个人投资者","华夏上证50ETF联接","8.2期末基金管理人的从业人员持有本基金的情况"}.All(x=>header.Contains(x,StringComparison.Ordinal))
                ||!next.Contains("开放式基金份额变动",StringComparison.Ordinal)||header.Contains("期末上市基金前十名持有人",StringComparison.Ordinal)||next.Contains("期末上市基金前十名持有人",StringComparison.Ordinal))
                throw new InvalidDataException("中期汇总披露范围与已核版式不符。");
            return new(document,Version,"NotDisclosed","本中报仅披露机构、个人及联接基金的汇总结构，未列具名账户；不能据此认定国家队零持仓、减持或清仓。",
                profile.ReportAsOf,profile.Publication,profile.HeaderPage,"份",null,"AggregateOnlyNoNamedTable",[]);
        }
        if(!new[]{"9.2期末上市基金前十名持有人","持有人名称","持有份额（份）","占上市总份额比例"}.All(x=>header.Contains(x,StringComparison.Ordinal))
            ||!Text(profile.Parts[^1].Page).Contains("9.3期末基金管理人的从业人员持有本基金的情况",StringComparison.Ordinal))
            throw new InvalidDataException("持有人表标题、单位、分母或结束边界不符。");
        var rows=new List<EtfHolderCandidate>();
        foreach(var part in profile.Parts)
        {
            var page=pages.Single(x=>x.Number==part.Page);
            for(var i=0;i<part.Bounds.Length-1;i++)
            {
                token.ThrowIfCancellationRequested();
                string Cell(double left,double right)=>PdfTextLayout.Cell(page,left,right,part.Bounds[i+1],part.Bounds[i],token);
                var rank=part.FirstRank+i;var rawName=Cell(134,318);var name=Compact(rawName);
                var shares=Compact(Cell(318,445));var percent=Compact(Cell(445,525));
                if(Compact(Cell(71,134))!=rank.ToString(CultureInfo.InvariantCulture)||name.Length is <2 or >200||rows.Any(x=>x.RegisteredHolder==name)
                    ||!Regex.IsMatch(shares,@"^\d{1,3}(,\d{3})*\.\d{2}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
                    ||!Regex.IsMatch(percent,@"^\d{1,3}\.\d{2}%$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                    throw new InvalidDataException("持有人行号、账户或数字列异常，拒绝返回半张表。");
                var quantity=decimal.Parse(shares,NumberStyles.AllowThousands|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
                var ratio=decimal.Parse(percent[..^1],CultureInfo.InvariantCulture);
                if(quantity<=0||ratio<=0||ratio>100)throw new InvalidDataException("持有人份额或比例范围无效。");
                rows.Add(new(rank,name,rawName,quantity,ratio,$"PDF物理第{part.Page}页，第9.2节，第{rank}行",InstitutionAccounts.CandidateGroup(name)));
            }
        }
        // 第11行联接基金独立保留；不能由共同登记名或表内份额推断经济所有权及账户独立性。
        if(rows.Count!=11||!rows.Select(x=>x.Rank).SequenceEqual(Enumerable.Range(1,11))||rows.Sum(x=>x.PercentOfListedUnits)>100
            ||!rows[^1].RegisteredHolder.EndsWith("联接基金",StringComparison.Ordinal))
            throw new InvalidDataException("完整持有人表、续表或另列联接基金规则不符。");
        return new(document,Version,"NeedsReview","完整11行具名持有人候选，保留另列联接基金；仍需核对法人、账户和独立持有关系，公告仅有日期，不补造准确时刻。",
            profile.ReportAsOf,profile.Publication,profile.HeaderPage,"份","ListedFundUnits","ListedTopTenTableWithSeparateFeederRow",rows);
    }
    private static string Compact(string value)=>string.Concat(value.Where(x=>!char.IsWhiteSpace(x)));
}
