using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CRS.Documents.Pdf;
using CRS.Plugins.NationalTeamWatch.Domain;

namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>仅接受已核159919年报；跨页续表和跨页账户名称一起读取，不批准机构持仓。</summary>
public static class HarvestCsi300HolderPdf
{
    public const string Version=EtfHolderParserVersions.HarvestCsi300;
    public const string Annual2024Url="https://disc.static.szse.cn/download/disc/disk03/finalpage/2025-03-27/34925050-1493-4ba3-8b4b-60bdb539f722.PDF";
    public const string Annual2025Url="https://disc.static.szse.cn/download/disc/disk03/finalpage/2026-03-30/3dc031f4-ee7d-4f9e-9a8b-22b212a0adf9.PDF";
    private const string FundName="嘉实沪深300交易型开放式指数证券投资基金";
    private const string FeederName=FundName+"联接基金(LOF)";
    private sealed record Segment(int Page,int FirstRank,double[] TopEdges);
    private sealed record Profile(string Hash,string Url,int Year,DateOnly Publication,int HeaderPage,Segment[] Segments,bool ContinuedFeederName);
    private static readonly Profile[] Profiles=[
        new("FBF084A3F0F35CC8BD89B3771F3C7776CBD2490BCA81129F40950DADD9090208",Annual2024Url,2024,new(2025,3,28),62,
            [new(62,1,[386.81,418.73,450.41,497.71,544.99,576.67,623.95,655.78,687.46,719.14,750.816,766.896])],true),
        new("08A66F0E3B1EA5FA1D9E16FE811E489B1A73A21FBB122D20D7940F78839D2335",Annual2025Url,2025,new(2026,3,30),66,
            [new(66,1,[684.468,716.24,747.94,779.54]),new(67,4,[72.24,103.94,151.24,182.94,230.24,261.94,309.24,340.94,388.24])],false)];

    public static EtfHolderCandidateReport Parse(EtfHolderDocument captured,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var document=captured.Document;
        if(captured.Content.Length is <5 or >8*1024*1024||!captured.Content.AsSpan(0,5).SequenceEqual("%PDF-"u8)
            ||!Convert.ToHexString(SHA256.HashData(captured.Content)).Equals(document.Hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("持有人原件身份或容量异常。");
        var profile=Profiles.SingleOrDefault(x=>x.Hash.Equals(document.Hash,StringComparison.OrdinalIgnoreCase));
        if(profile is null)return new(document,Version,"UnsupportedTemplate","未登记该原件的159919持有人版式；没有解析结果不代表主体零持仓。",null,null,null,null,null,null,[]);
        if(document.Market!="SZSE"||document.Ticker!="159919"||!Uri.TryCreate(document.Url,UriKind.Absolute,out var uri)
            ||uri.AbsoluteUri!=profile.Url||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0)
            throw new InvalidDataException("持有人原件与登记证券或官方路径不匹配。");
        if(!DateTimeOffset.TryParse(document.FirstSeenAt,CultureInfo.InvariantCulture,DateTimeStyles.None,out var firstSeen)
            ||DateOnly.FromDateTime(firstSeen.ToOffset(TimeSpan.FromHours(8)).DateTime)<profile.Publication)
            throw new InvalidDataException("持有人原件首见不能早于已登记公告日期。");
        var pages=PdfReader.ReadSelected(captured.Content,[1,profile.HeaderPage,profile.HeaderPage+1],token);
        var cover=Compact(PdfTextLayout.Lines(pages.Single(x=>x.Number==1).Letters,token));
        if(!cover.Contains(FundName,StringComparison.Ordinal)||!cover.Contains(profile.Year+"年年度报告",StringComparison.Ordinal)
            ||!cover.Contains(profile.Year+"年12月31日",StringComparison.Ordinal)
            ||!cover.Contains("送出日期："+profile.Publication.ToString("yyyy年M月d日",CultureInfo.InvariantCulture),StringComparison.Ordinal))
            throw new InvalidDataException("持有人报告封面、报告期或公告日期不符。");
        var header=Compact(PdfTextLayout.Lines(pages.Single(x=>x.Number==profile.HeaderPage).Letters,token));
        var following=pages.Single(x=>x.Number==profile.HeaderPage+1);
        var followingText=Compact(PdfTextLayout.Lines(following.Letters,token));
        if(!new[]{"9.2期末上市基金前十名持有人","持有人名称","持有份额（份）","占上市总份额比例（%）"}.All(x=>header.Contains(x,StringComparison.Ordinal))
            ||!followingText.Contains("上述基金持有人份额均为场内基金份额",StringComparison.Ordinal)
            ||!followingText.Contains("9.3期末基金管理人的从业人员持有本基金的情况",StringComparison.Ordinal))
            throw new InvalidDataException("持有人表标题、单位、分母或续表范围脚注不符。");
        var rows=new List<EtfHolderCandidate>();
        foreach(var segment in profile.Segments)
        {
            var page=pages.Single(x=>x.Number==segment.Page);
            for(var i=0;i<segment.TopEdges.Length-1;i++)
            {
                token.ThrowIfCancellationRequested();var rank=segment.FirstRank+i;
                string Cell(double left,double right)=>PdfTextLayout.Cell(page,left,right,page.Height-segment.TopEdges[i+1],page.Height-segment.TopEdges[i],token);
                var rankText=Compact(Cell(71,150.1));var rawName=Cell(150.1,263.5);
                var shares=Compact(Cell(263.5,363.5));var percent=Compact(Cell(363.5,524.5));
                var evidence=$"PDF物理第{segment.Page}页，第9.2节，第{rank}行";
                if(profile.ContinuedFeederName&&rank==11)
                {
                    string Continuation(double left,double right)=>PdfTextLayout.Cell(following,left,right,following.Height-103.94,following.Height-72.24,token);
                    if(Compact(Continuation(71,150.1)+Continuation(263.5,363.5)+Continuation(363.5,524.5)).Length!=0)
                        throw new InvalidDataException("账户续行含额外行号或数量，拒绝串接另一持有人。");
                    var tail=Continuation(150.1,263.5);
                    if(Compact(tail)!="放式指数证券投资基金联接基金(LOF)")throw new InvalidDataException("联接基金跨页账户续行不完整。");
                    rawName+="\n"+tail;evidence=$"PDF物理第{segment.Page}至{following.Number}页，第9.2节，第11行";
                }
                var name=Compact(rawName);
                if(rankText!=rank.ToString(CultureInfo.InvariantCulture)||name.Length is <2 or >200||rows.Any(x=>x.RegisteredHolder==name)
                    ||!Regex.IsMatch(shares,@"^\d{1,3}(,\d{3})*\.\d{2}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100))
                    ||!Regex.IsMatch(percent,@"^\d{1,3}\.\d{2}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                    throw new InvalidDataException("持有人表行号、账户或数字列异常，拒绝返回半张表。");
                var quantity=decimal.Parse(shares,NumberStyles.AllowThousands|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture);
                var ratio=decimal.Parse(percent,CultureInfo.InvariantCulture);
                if(quantity<=0||ratio<=0||ratio>100)throw new InvalidDataException("持有人份额或比例范围无效。");
                rows.Add(new(rank,name,rawName,quantity,ratio,evidence,InstitutionAccounts.CandidateGroup(name)));
            }
        }
        if(rows.Count!=11||!rows.Select(x=>x.Rank).SequenceEqual(Enumerable.Range(1,11))||rows.Sum(x=>x.PercentOfListedUnits)>100||rows[^1].RegisteredHolder!=FeederName)
            throw new InvalidDataException("完整持有人表、连续序号或另列联接基金不符。");
        return new(document,Version,"NeedsReview","11条完整登记持有人候选，含跨页续表及另列联接基金。仍须核验法人、账户和独立持有关系；不能推断当日资金流。",
            new(profile.Year,12,31),profile.Publication,profile.HeaderPage,"份","ListedFundUnits","ListedTopTenTableWithSeparateFeederRow",rows);
    }
    private static string Compact(string value)=>string.Concat(value.Where(x=>!char.IsWhiteSpace(x)));
}
