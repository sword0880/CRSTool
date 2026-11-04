using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CRS.Documents.Pdf;
using CRS.Plugins.NationalTeamWatch.Domain;
namespace CRS.Plugins.NationalTeamWatch.Infrastructure;

/// <summary>原文期末总股数、账户、股类及表格边界机械核对；无限售数量不能冒充账户总A股持仓。</summary>
public static class AutomaticEquityPdf
{
    public const string Version="automatic-equity-a-table-v1";
    private sealed record Line(double Y,string Text,double[] Starts,double[] Ends);
    private sealed record Column(double Left,double Right,string Text);
    private sealed record Row(string Account,decimal Shares,string ShareClass,int Page);
    private sealed record Anchor(double Y,decimal Shares,double Left,double End,double Right);
    private static readonly Regex Number=new(@"(?<![0-9.,])(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?![0-9.,])",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static string Compact(string value)=>Regex.Replace(value,@"\s+","",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static AutomaticEquityProof Parse(AutomaticEquityDocument document,byte[] bytes,CancellationToken token=default)
    {
        if(document.Ticker=="603195"||document.ReportAsOf>document.Published||!AutomaticEquitySource.IsPdf(new(document.Url)))
            throw new InvalidDataException("证券受固定来源限制或原文身份无效。");
        var pages=PdfReader.ReadFirst(bytes,32,token);
        var text=Compact(string.Join("\n",pages.Select(x=>PdfTextLayout.Lines(x.Letters,token))));
        var cover=Compact(string.Join("\n",pages.Take(2).Select(x=>PdfTextLayout.Lines(x.Letters,token))));
        var kind=document.ReportAsOf.Month switch{3=>"第一季度",6=>"半年度",9=>"第三季度",12=>"年度",_=>throw new InvalidDataException("报告截止月无效。")};
        if(!cover.Contains($"{document.ReportAsOf.Year}年{kind}报告",StringComparison.Ordinal))
            throw new InvalidDataException("原文报告类型不能独立核对。");
        var date=$@"{document.ReportAsOf.Year}年0?{document.ReportAsOf.Month}月0?{document.ReportAsOf.Day}日";
        if(!text.Contains(document.Ticker,StringComparison.Ordinal)||!text.Contains(Compact(document.Name.TrimStart('*')),StringComparison.Ordinal)
            ||!Regex.IsMatch(text,date,RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
            throw new InvalidDataException("原文证券名称、代码或报告截止日不能独立核对。");
        var ordinary=ReadOrdinary(pages,token);var unlimited=ReadUnlimited(pages,token);
        var holders=new List<EquityHolder>();var accounts=new List<AutomaticEquityAccount>();var issues=new List<string>();
        foreach(var row in ordinary)
        {
            var group=Identity(row.Account);
            if(group is null)
            {
                if(InstitutionAccounts.CandidateGroup(row.Account) is not null)issues.Add(row.Account+"：组合主体待核");
                continue;
            }
            if(row.ShareClass=="H股")continue;
            var confirmed=row.ShareClass is "A股" or "人民币普通股";
            if(!confirmed)confirmed=unlimited.Any(x=>x.Account==row.Account&&(x.ShareClass is "A股" or "人民币普通股")&&x.Shares==row.Shares);
            if(!confirmed){issues.Add(row.Account+"：总股数与A股口径无法对应");continue;}
            if(holders.Any(x=>x.Account==row.Account))throw new InvalidDataException("同一A股账户有重复数量。");
            holders.Add(new(row.Account,row.Shares,null));accounts.Add(new(row.Account,group,$"PDF P{row.Page}普通股表；期末总股数及A股股类交叉核对，股"));
        }
        foreach(var row in unlimited.Where(x=>Identity(x.Account) is not null&&(x.ShareClass is "A股" or "人民币普通股")))
            if(!ordinary.Any(x=>x.Account==row.Account))issues.Add(row.Account+"：仅有无限售股数，账户总A股数未确认");
        if(accounts.Count==0&&(issues.Count>0||new[]{"社保基金","基本养老保险基金","中央汇金","中国证券金融","国家集成电路产业投资基金"}.Any(x=>text.Contains(x,StringComparison.Ordinal))))
            throw new InvalidDataException("原文机构线索未对应清楚总A股持仓，不能判为无匹配。");
        return new(holders,accounts,"PDF P"+string.Join(",",ordinary.Select(x=>x.Page).Distinct())+"；期末普通股表中的已核A股账户子集；比例、性质及可比性未知",issues);
    }
    private static string? Identity(string name)=>InstitutionAccounts.Group(name)??InstitutionAccounts.AnnotatedGroup(name)??(name switch
    {
        InstitutionAccounts.ChipFund2ManagedAccount=>"国家大基金",
        "中信证券股份有限公司－社保基金1106组合" or "嘉实基金管理有限公司－社保基金16042组合"=>"社保基金",
        _=>null
    });
    private static IReadOnlyList<Row> ReadOrdinary(IReadOnlyList<PdfPageContent> pages,CancellationToken token)
    {
        var result=new List<Row>();var names=new HashSet<string>(StringComparer.Ordinal);
        Column? nameColumn=null,quantityColumn=null,classColumn=null;var first=0;double start=0;
        foreach(var page in pages)
        {
            var lines=Lines(page);
            if(nameColumn is null)
            {
                var heading=lines.FirstOrDefault(x=>Regex.IsMatch(Compact(x.Text),@"前(?:10|十)名(?:普通股)?股东持股情况",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)));
                if(heading is null)continue;
                var columns=HeaderColumns(page,heading.Y,token);
                nameColumn=Unique(columns,c=>c.Text.Contains("股东名称",StringComparison.Ordinal));
                quantityColumn=Unique(columns,c=>(c.Text.Contains("持股数量",StringComparison.Ordinal)||c.Text.Contains("持股总数",StringComparison.Ordinal)||c.Text.Contains("期末持股数",StringComparison.Ordinal))
                    &&!c.Text.Contains("有限售",StringComparison.Ordinal)&&!c.Text.Contains("质押",StringComparison.Ordinal));
                classColumn=Unique(columns,c=>c.Text.Contains("股份类别",StringComparison.Ordinal)||c.Text.Contains("股份种类",StringComparison.Ordinal));
                if(nameColumn is null||quantityColumn is null)throw new InvalidDataException("普通股表账户或期末总股数列无法唯一对应。");
                var unit=quantityColumn.Text.Contains("（股）",StringComparison.Ordinal)||quantityColumn.Text.Contains("(股)",StringComparison.Ordinal)
                    ||lines.Any(x=>Math.Abs(x.Y-heading.Y)<70&&Compact(x.Text).Contains("单位：股",StringComparison.Ordinal));
                if(!unit||quantityColumn.Text.Contains("万股",StringComparison.Ordinal)||quantityColumn.Text.Contains("千股",StringComparison.Ordinal))
                    throw new InvalidDataException("普通股表总股数单位不是已核的股。");
                first=page.Number;start=heading.Y;
            }
            if(page.Number>first+2)break;
            foreach(var line in lines.Where(l=>page.Number!=first||l.Y<start-5))
            {
                var number=Compact(PdfTextLayout.Cell(page,quantityColumn!.Left+.5,quantityColumn.Right-.5,Math.Max(0,line.Y-1),Math.Min(page.Height,line.Y+1),token));
                if(!Regex.IsMatch(number,@"^(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))continue;
                var shares=decimal.Parse(number,NumberStyles.AllowThousands,CultureInfo.InvariantCulture);
                if(shares<=0)throw new InvalidDataException("普通股总股数为零或负数。");
                var nameBox=Bounds(page,(nameColumn.Left+nameColumn.Right)/2,line.Y);
                var qtyBox=Bounds(page,(quantityColumn.Left+quantityColumn.Right)/2,line.Y);
                var name=Compact(PdfTextLayout.Cell(page,nameColumn.Left+.5,nameColumn.Right-.5,nameBox.Bottom+.3,nameBox.Top-.3,token));
                var shareClass=classColumn is null?"Unknown":Compact(PdfTextLayout.Cell(page,classColumn.Left+.5,classColumn.Right-.5,qtyBox.Bottom+.3,qtyBox.Top-.3,token));
                if(string.IsNullOrWhiteSpace(name)||name.Length>200)throw new InvalidDataException("普通股账户边界不完整。");
                names.Add(name);result.Add(new(name,shares,shareClass,page.Number));
                if(names.Count==10)return result;
                if(result.Count>20)throw new InvalidDataException("普通股账户与股类行超出范围。");
            }
        }
        throw new InvalidDataException("期末普通股表未完整对应十名股东。");
    }
    private static IReadOnlyList<Row> ReadUnlimited(IReadOnlyList<PdfPageContent> pages,CancellationToken token)
    {
        var rows=new List<Row>();var inTable=false;var first=0;
        foreach(var page in pages)
        {
            var lines=Lines(page);var heading=lines.FirstOrDefault(x=>Regex.IsMatch(Compact(x.Text),@"前(?:10|十)名无限售条件(?:普通股)?股东持股情况",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)));
            if(heading is not null){inTable=true;first=page.Number;}
            if(!inTable||page.Number>first+2)continue;
            var bottom=lines.Where(x=>(heading is null||x.Y<heading.Y)&&Regex.IsMatch(Compact(x.Text),@"^(上述股东|持股5%|前10名股东及|三、|四、|（二）)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100)))
                .Select(x=>x.Y).DefaultIfEmpty(0).Max();
            foreach(var line in lines.Where(x=>(heading is null||x.Y<heading.Y)&&x.Y>bottom))
            {
                token.ThrowIfCancellationRequested();var anchor=TryAnchor(line);if(anchor is null)continue;
                var xs=page.Rules.Where(r=>Math.Abs(r.X1-r.X2)<.5&&Math.Min(r.Y1,r.Y2)<anchor.Y&&Math.Max(r.Y1,r.Y2)>anchor.Y).Select(r=>r.X1).Distinct().Order().ToArray();
                var right=xs.Where(x=>x<anchor.Left-1).DefaultIfEmpty(double.NaN).Max();
                var left=xs.Where(x=>x<right-1).DefaultIfEmpty(double.NaN).Max();
                if(!double.IsFinite(left)||!double.IsFinite(right))continue;
                var box=Bounds(page,(left+right)/2,anchor.Y);
                var name=Compact(PdfTextLayout.Cell(page,left+.3,right-.3,box.Bottom+.3,box.Top-.3,token));
                var shareClass=Compact(PdfTextLayout.Cell(page,anchor.End+1,anchor.Right-1,box.Bottom+.3,box.Top-.3,token));
                if(shareClass is "A股" or "人民币普通股" or "H股")rows.Add(new(name,anchor.Shares,shareClass,page.Number));
                if(rows.Count>20)throw new InvalidDataException("无限售表超出账户范围。");
            }
            if(bottom>0)break;
        }
        return rows;
    }
    private static Column? Unique(IReadOnlyList<Column> columns,Func<Column,bool> predicate)
    {var selected=columns.Where(predicate).ToArray();return selected.Length==1?selected[0]:null;}
    private static IReadOnlyList<Column> HeaderColumns(PdfPageContent page,double heading,CancellationToken token)
    {
        var xs=page.Rules.Where(r=>Math.Abs(r.X1-r.X2)<.5&&Math.Max(r.Y1,r.Y2)>heading-140&&Math.Min(r.Y1,r.Y2)<heading+10)
            .Select(r=>Math.Round(r.X1*2)/2).Distinct().Order().ToArray();var columns=new List<Column>();
        for(var i=0;i+1<xs.Length;i++)if(xs[i+1]-xs[i]>8)
            columns.Add(new(xs[i],xs[i+1],Compact(PdfTextLayout.Cell(page,xs[i]+.5,xs[i+1]-.5,Math.Max(0,heading-140),Math.Min(page.Height,heading+10),token))));
        return columns;
    }
    private static (double Top,double Bottom) Bounds(PdfPageContent page,double middle,double y)
    {
        var borders=page.Rules.Where(r=>Math.Abs(r.Y1-r.Y2)<.5&&Math.Min(r.X1,r.X2)<=middle&&Math.Max(r.X1,r.X2)>=middle).Select(r=>r.Y1).ToArray();
        var top=borders.Where(v=>v>y+1).DefaultIfEmpty(double.NaN).Min();var bottom=borders.Where(v=>v<y-1).DefaultIfEmpty(double.NaN).Max();
        if(!double.IsFinite(top)||!double.IsFinite(bottom)||top-bottom>150)throw new InvalidDataException("原文单元格边界不完整。");
        return(top,bottom);
    }
    private static Line[] Lines(PdfPageContent page)=>page.Letters.GroupBy(x=>Math.Round(x.BaselineY/1.5)*1.5).OrderByDescending(x=>x.Key).Select(x=>
    {
        var text=new StringBuilder();var starts=new List<double>();var ends=new List<double>();double? last=null;
        foreach(var letter in x.OrderBy(y=>y.X))
        {
            var value=Compact(letter.Value);if(value.Length==0)continue;
            if(last is {} right&&letter.X-right>10){text.Append(' ');starts.Add(letter.X);ends.Add(letter.X);}
            text.Append(value);foreach(var _ in value){starts.Add(letter.X);ends.Add(letter.Right);}last=letter.Right;
        }
        return new Line(x.Key,text.ToString(),starts.ToArray(),ends.ToArray());
    }).ToArray();
    private static Anchor? TryAnchor(Line line)
    {
        var values=Number.Matches(line.Text);if(values.Count<2)return null;var one=values[^2];var two=values[^1];
        if(one.Value!=two.Value||!decimal.TryParse(one.Value,NumberStyles.AllowThousands,CultureInfo.InvariantCulture,out var shares)||shares<=0)return null;
        var start=line.Starts[one.Index];var end=line.Ends[one.Index+one.Length-1];var right=line.Starts[two.Index];
        return right-end>10?new(line.Y,shares,start,end,right):null;
    }
}
