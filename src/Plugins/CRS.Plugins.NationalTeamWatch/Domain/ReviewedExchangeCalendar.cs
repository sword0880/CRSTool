namespace CRS.Plugins.NationalTeamWatch.Domain;

/// <summary>仅支持已核对的交易所年度公告；节日闭市与周末共同决定计划交易日。</summary>
public static class ReviewedExchangeCalendar
{
    public static Uri Source(string market,int year)=>year==2026?market switch
    {
        "SSE"=>new("https://www.sse.com.cn/disclosure/announcement/general/c/c_20251222_10802507.shtml"),
        "SZSE"=>new("https://www.szse.cn/disclosure/notice/general/t20251222_618087.html"),
        _=>throw new ArgumentException("不支持的市场。")
    }:throw new InvalidDataException("该年度尚未登记已核对的官方休市公告。");

    public static IReadOnlyList<(DateOnly Date,bool Open)> Parse(string market,int year,string text)
    {
        _=Source(market,year);
        // 页面结构/公告内容变化时停止，不把HTTP200或空网页当成日历。
        var compact=System.Text.RegularExpressions.Regex.Replace(System.Net.WebUtility.HtmlDecode(text),@"<[^>]*>|\s+","",System.Text.RegularExpressions.RegexOptions.None,TimeSpan.FromSeconds(1));
        if(!compact.Contains("2026年部分节假日休市安排") || !compact.Contains("休市") || !compact.Contains("周末休市"))throw new InvalidDataException("休市公告标题或内容不匹配。");
        var ranges=new[]{(1,1,1,3),(2,15,2,23),(4,4,4,6),(5,1,5,5),(6,19,6,21),(9,25,9,27),(10,1,10,7)};
        var actual=System.Text.RegularExpressions.Regex.Matches(compact,@"(\d+)月(\d+)日（星期.）至(\d+)月(\d+)日（星期.）休市",System.Text.RegularExpressions.RegexOptions.None,TimeSpan.FromSeconds(1));
        if(actual.Count!=ranges.Length || actual.Select(m=>(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),int.Parse(m.Groups[3].Value),int.Parse(m.Groups[4].Value))).Where((r,i)=>r!=ranges[i]).Any())throw new InvalidDataException("官方节假日范围与已核对版本不符。");
        var closed=ranges.SelectMany(r=>Enumerable.Range(new DateOnly(year,r.Item1,r.Item2).DayNumber,new DateOnly(year,r.Item3,r.Item4).DayNumber-new DateOnly(year,r.Item1,r.Item2).DayNumber+1)).ToHashSet();
        return Enumerable.Range(new DateOnly(year,1,1).DayNumber,DateTime.IsLeapYear(year)?366:365).Select(n=>
        {var day=DateOnly.FromDayNumber(n);return(day,day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !closed.Contains(n));}).ToArray();
    }
}
