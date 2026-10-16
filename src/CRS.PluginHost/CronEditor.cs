using System.Globalization;
using Quartz;

namespace CRS.PluginHost;

public enum CronFrequency { Seconds, Minutes, Hourly, Daily, Weekly, Monthly, Manual }
public sealed record CronOptions(CronFrequency Frequency= CronFrequency.Daily,int Interval=5,int Hour=23,int Minute=15,int Second=0,int Day=1,string Weekdays="MON");
public sealed record CronPreview(bool Valid,string Message,IReadOnlyList<DateTimeOffset> Next);

/// <summary>与宿主共用 Quartz 方言；不创建调度器、不保存计划、不执行任务。</summary>
public static class CronEditor
{
    public static readonly string[] DayNames=["MON","TUE","WED","THU","FRI","SAT","SUN"];
    public static string Generate(CronOptions value)
    {
        if(value.Second is <0 or >59 || value.Minute is <0 or >59 || value.Hour is <0 or >23 || value.Day is <1 or >31 || value.Interval is <1 or >59)
            throw new ArgumentException("时间或间隔超出允许范围。");
        var weekdays=value.Weekdays.Split(',');
        if(value.Frequency==CronFrequency.Weekly && (weekdays.Length==0 || weekdays.Any(x=>!DayNames.Contains(x)) || weekdays.Distinct().Count()!=weekdays.Length))
            throw new ArgumentException("每周计划至少选择一天，且不能重复。");
        return value.Frequency switch
        {
            CronFrequency.Seconds=>$"0/{value.Interval} * * * * ?",
            CronFrequency.Minutes=>$"{value.Second} 0/{value.Interval} * * * ?",
            CronFrequency.Hourly=>$"{value.Second} {value.Minute} * * * ?",
            CronFrequency.Daily=>$"{value.Second} {value.Minute} {value.Hour} * * ?",
            CronFrequency.Weekly=>$"{value.Second} {value.Minute} {value.Hour} ? * {value.Weekdays}",
            CronFrequency.Monthly=>$"{value.Second} {value.Minute} {value.Hour} {value.Day} * ?",
            _=>throw new ArgumentException("手动模式不生成表达式。")
        };
    }
    public static CronOptions Read(string expression)
    {
        var parts=expression.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if(parts.Length!=6)return new(CronFrequency.Manual);
        foreach(var mode in Enum.GetValues<CronFrequency>().Where(x=>x!=CronFrequency.Manual))
        {
            var candidate=new CronOptions(mode,ParseStep(mode==CronFrequency.Seconds?parts[0]:parts[1]),Parse(parts[2],23),Parse(parts[1],15),Parse(parts[0],0),Parse(parts[3],1),parts[5]);
            try
            {
                var canonical=Generate(candidate);
                if(mode is CronFrequency.Seconds or CronFrequency.Minutes && candidate.Interval==1)
                {
                    var fields=canonical.Split(' ');var stepField=mode==CronFrequency.Seconds?0:1;
                    if(parts[stepField]=="*")fields[stepField]="*";
                    canonical=string.Join(' ',fields);
                }
                if(canonical==string.Join(' ',parts))return candidate;
            }
            catch(ArgumentException){ }
        }
        return new(CronFrequency.Manual);
    }
    private static int Parse(string value,int fallback)=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var number)?number:fallback;
    private static int ParseStep(string value)=>value=="*"?1:value.StartsWith("0/",StringComparison.Ordinal)?Parse(value[2..],5):5;
    public static string Describe(CronOptions value)
    {
        var time=$"{value.Hour:00}:{value.Minute:00}:{value.Second:00}";
        var labels=new[]{"周一","周二","周三","周四","周五","周六","周日"};
        return value.Frequency switch
        {
            CronFrequency.Seconds=>$"每分钟从第 0 秒起，每隔 {value.Interval} 秒触发；跨分钟重新计数。",
            CronFrequency.Minutes=>$"每小时从第 0 分起，每隔 {value.Interval} 分钟，在第 {value.Second} 秒触发；跨小时重新计数。",
            CronFrequency.Hourly=>$"每小时的第 {value.Minute} 分 {value.Second} 秒触发。",
            CronFrequency.Daily=>$"每天 {time} 触发。",
            CronFrequency.Weekly=>$"每周{string.Join("、",DayNames.Select((x,i)=>(x,i)).Where(x=>value.Weekdays.Split(',').Contains(x.x)).Select(x=>labels[x.i]))} {time} 触发；不自动排除节假日。",
            CronFrequency.Monthly=>$"每月 {value.Day} 日 {time} 触发；没有该日期的月份跳过。",
            _=>"手动编辑完整 Quartz 表达式，支持范围、列表及高级规则。"
        };
    }
    public static CronPreview Preview(string expression,string zoneId,DateTimeOffset after,int count=5)
    {
        if(string.IsNullOrWhiteSpace(expression))return new(false,"请填写 Cron 表达式。",[]);
        if(expression.Length>120)return new(false,"Cron 表达式不能超过 120 个字符。",[]);
        if(count is <1 or >10)throw new ArgumentOutOfRangeException(nameof(count));
        CronExpression cron;
        try{cron=new(expression);}
        catch(FormatException){return new(false,"表达式无效：请使用 Quartz 格式（秒 分 时 日 月 星期，可选年份）。",[]);}
        TimeZoneInfo zone;
        try{zone=TimeZoneInfo.FindSystemTimeZoneById(zoneId.Trim());}
        catch(Exception e) when(e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException){return new(false,"时区无效，请填写有效时区，例如 China Standard Time。",[]);}
        cron=cron.WithTimeZone(zone);
        var next=new List<DateTimeOffset>();var cursor=after;
        for(var i=0;i<count;i++)
        {
            var instant=cron.GetNextValidTimeAfter(cursor);
            if(instant is null)break;
            next.Add(TimeZoneInfo.ConvertTime(instant.Value,zone));cursor=instant.Value;
        }
        return new(true,next.Count==0?"表达式有效，但未来没有符合条件的计划时间。":"表达式有效；以下按所选时区显示，实际执行仍受暂停、退避与服务状态影响。",next);
    }
}
