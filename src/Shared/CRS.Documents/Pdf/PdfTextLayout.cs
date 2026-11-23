namespace CRS.Documents.Pdf;

/// <summary>按基线拼接文字或截取矩形单元格；仅处理版式，不解释账户、金额或报告口径。</summary>
public static class PdfTextLayout
{
    public static string Lines(IEnumerable<PdfLetter> letters,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();var rows=new Dictionary<double,List<PdfLetter>>();var count=0;
        foreach(var letter in letters)
        {
            cancellation.ThrowIfCancellationRequested();
            if(++count>2_000_000||!double.IsFinite(letter.X)||!double.IsFinite(letter.BaselineY)||!double.IsFinite(letter.Right))
                throw new InvalidDataException("PDF文字坐标或数量异常。");
            var baseline=Math.Round(letter.BaselineY/1.5)*1.5;
            if(!rows.TryGetValue(baseline,out var row)){row=[];rows.Add(baseline,row);}
            row.Add(letter);
        }
        return string.Join('\n',rows.OrderByDescending(x=>x.Key).Select(x=>string.Concat(x.Value.OrderBy(y=>y.X).Select(y=>y.Value))));
    }

    /// <summary>矩形采用页面左下角坐标；按字符起点取左闭右开列、上下开区间，保留原始换行。</summary>
    public static string Cell(PdfPageContent page,double left,double right,double bottom,double top,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(!double.IsFinite(left)||!double.IsFinite(right)||!double.IsFinite(bottom)||!double.IsFinite(top)
            ||left<0||bottom<0||left>=right||bottom>=top||right>page.Width||top>page.Height)
            throw new InvalidDataException("PDF单元格矩形超出页面或顺序无效。");
        return Lines(page.Letters.Where(x=>x.X>=left&&x.X<right&&x.BaselineY>bottom&&x.BaselineY<top),cancellation);
    }
}
