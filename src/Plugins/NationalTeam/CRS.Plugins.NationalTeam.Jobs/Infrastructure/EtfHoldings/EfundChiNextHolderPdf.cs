using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CRS.Documents.Pdf;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>已核版式的159915原件；年度具名表与中期仅汇总披露分开，解析不赋予人工确认。</summary>
public static class EfundChiNextHolderPdf
{
    public const string Version=EtfHolderParserVersions.EfundChiNext;
    public const string Annual2024Url="https://disc.static.szse.cn/download/disc/disk03/finalpage/2025-03-30/e9230566-351d-49a5-afa9-fa465d0c7fa5.PDF";
    public const string Annual2025Url="https://disc.static.szse.cn/download/disc/disk03/finalpage/2026-03-31/33722948-c0d2-4271-82b3-1f2f6921a3a2.PDF";
    public const string Interim2026Url="https://disc.static.szse.cn/download/disc/disk03/finalpage/2026-08-31/22c71a78-d740-42e7-9f92-3078b33383a2.PDF";
    private const string FundName="易方达创业板交易型开放式指数证券投资基金";
    private sealed record Profile(string Hash,string Url,DateOnly ReportAsOf,DateOnly Publication,string CoverKind,int HeaderPage,int TablePage,double[]? Rows);
    private static readonly Profile[] Profiles=[
        new("E1B5D9AC6B7F80FA39C744B1B22A3427FE9EF3917F93D0BD2D81E2393B0E6112",Annual2024Url,new(2024,12,31),new(2025,3,31),"2024年年度报告",62,63,
            [770,752,736,704,672,656,640,624,608,592,576,529]),
        new("E4C30626ED19C647D80C78B86FF2BF0BE93F8E08FD29C9DD185C8F0CF5EEE900",Annual2025Url,new(2025,12,31),new(2026,3,31),"2025年年度报告",64,64,
            [676,657,641,625,594,562,546,514,482,466,450,403]),
        new("E0F0169A8A40E9EF36B569CA5F26536BFE8FCD1C9D5A9C46DAA8EC2D7EEC27AD",Interim2026Url,new(2026,6,30),new(2026,8,31),"2026年中期报告",47,48,null)];

    public static EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var document=captured.Document;
        if(captured.Content.Length is <5 or >8*1024*1024||!captured.Content.AsSpan(0,5).SequenceEqual("%PDF-"u8)
            ||!Convert.ToHexString(SHA256.HashData(captured.Content)).Equals(document.Hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("持有人原件身份或容量异常。");
        var profile=Profiles.SingleOrDefault(x=>x.Hash.Equals(document.Hash,StringComparison.OrdinalIgnoreCase));
        if(profile is null)return new(document,Version,"UnsupportedTemplate","未登记该原件的159915持有人版式；没有解析结果不代表主体零持仓。",null,null,null,null,null,null,[]);
        if(document.Market!="SZSE"||document.Ticker!="159915"||!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)
            ||uri.AbsoluteUri!=profile.Url||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)
            throw new InvalidDataException("持有人原件与登记证券或官方路径不匹配。");
        if(!DateTimeOffset.TryParse(document.FirstSeenAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out var firstSeen)
            ||DateOnly.FromDateTime(firstSeen.ToOffset(TimeSpan.FromHours(8)).DateTime)<profile.Publication)
            throw new InvalidDataException("持有人原件首见不能早于已登记公告日期。");
        var pages=PdfReader.ReadSelected(captured.Content,new[]{1,profile.HeaderPage,profile.TablePage}.Distinct().ToArray(),token);
        string Text(int number)=>Compact(PdfTextLayout.Lines(pages.Single(x=>x.Number==number).Letters,token));
        var cover=Text(1);
        if(!cover.Contains(FundName,StringComparison.Ordinal)||!cover.Contains(profile.CoverKind,StringComparison.Ordinal))
            throw new InvalidDataException("持有人报告封面身份不符。");
        var header=Text(profile.HeaderPage);var table=pages.Single(x=>x.Number==profile.TablePage);var tableText=Text(profile.TablePage);
        if(profile.Rows is null)
        {
            if(!header.Contains("8.1期末基金份额持有人户数及持有人结构",StringComparison.Ordinal)||!header.Contains("机构投资者",StringComparison.Ordinal)
                ||!header.Contains("个人投资者",StringComparison.Ordinal)||!tableText.Contains("8.2期末基金管理人的从业人员持有本基金的情况",StringComparison.Ordinal)
                ||header.Contains("期末上市基金前十名持有人",StringComparison.Ordinal)||tableText.Contains("期末上市基金前十名持有人",StringComparison.Ordinal))
                throw new InvalidDataException("中期持有人汇总版式与已登记原件不符。");
            return new(document,Version,"NotDisclosed","本期仅披露持有人汇总结构，未列示具名持有人表；不是国家队零持仓或清仓证据，不能与年报具名份额直接比较。",
                profile.ReportAsOf,profile.Publication,profile.HeaderPage,"份",null,"AggregateOnlyNoNamedTable",[]);
        }
        if(!new[]{"9.2期末上市基金前十名持有人","持有人名称","持有份额（份）","占上市总份额比例"}.All(x=>header.Contains(x,StringComparison.Ordinal))
            ||!tableText.Contains("本表统计的上市基金前十名持有人均为场内持有人",StringComparison.Ordinal))
            throw new InvalidDataException("持有人表标题、单位、分母或范围脚注不符。");
        var rows=new List<EtfHolderCandidate>();
        for(var i=0;i<profile.Rows.Length-1;i++)
        {
            token.ThrowIfCancellationRequested();
            string Cell(double left,double right)=>PdfTextLayout.Cell(table,left,right,profile.Rows[i+1],profile.Rows[i],token);
            var rank=Compact(Cell(46,99));var rawName=Cell(99,283.5);var name=Compact(rawName);
            var shares=Compact(Cell(283.5,446));var percent=Compact(Cell(446,550));
            if(rank!=(i+1).ToString(CultureInfo.InvariantCulture)||name.Length is <2 or >200||rows.Any(x=>x.RegisteredHolder==name)
                ||!Regex.IsMatch(shares,@"^\d{1,3}(,\d{3})*\.\d{2}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
                ||!Regex.IsMatch(percent,@"^\d{1,3}\.\d{2}%$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                throw new InvalidDataException("持有人表行号、账户或数字列异常，拒绝返回半张表。");
            var quantity=decimal.Parse(shares,NumberStyles.AllowThousands|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
            var ratio=decimal.Parse(percent[..^1],CultureInfo.InvariantCulture);
            if(quantity<=0||ratio<=0||ratio>100)throw new InvalidDataException("持有人份额或比例范围无效。");
            rows.Add(new(i+1,name,rawName,quantity,ratio,$"PDF物理第{profile.TablePage}页，第9.2节，第{i+1}行",InstitutionAccounts.CandidateGroup(name)));
        }
        if(rows.Count!=11||rows.Sum(x=>x.PercentOfListedUnits)>100||!rows[^1].RegisteredHolder.Contains("联接基金",StringComparison.Ordinal))
            throw new InvalidDataException("完整持有人表或另列联接基金行不符。");
        return new(document,Version,"NeedsReview","11条登记持有人候选，含单独列示的联接基金行。需核验法人、账户及独立持有关系；不能推断受益人或当日资金流。",
            profile.ReportAsOf,profile.Publication,profile.TablePage,"份","ListedFundUnits","ListedTopTenTableWithSeparateFeederRow",rows);
    }
    private static string Compact(string value)=>string.Concat(value.Where(x=>!char.IsWhiteSpace(x)));
}
