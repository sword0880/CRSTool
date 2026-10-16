using CRS.PluginHost;

internal static class CronEditorChecks
{
    public static void Run()
    {
        var anchor=new DateTimeOffset(2026,10,16,0,0,0,TimeSpan.Zero);
        foreach(var mode in Enum.GetValues<CronFrequency>().Where(x=>x!=CronFrequency.Manual))
        {
            var options=new CronOptions(mode,7,23,15,12,31,"MON,FRI");
            var text=CronEditor.Generate(options);
            if(CronEditor.Read(text).Frequency!=mode || CronEditor.Generate(CronEditor.Read(text))!=text)throw new Exception("生成器还原错误："+mode);
            var preview=CronEditor.Preview(text,"China Standard Time",anchor);
            if(!preview.Valid || preview.Next.Count!=5 || preview.Next.Any(x=>x<=anchor || x.Offset!=TimeSpan.FromHours(8)) || preview.Next.Zip(preview.Next.Skip(1)).Any(x=>x.First>=x.Second))throw new Exception("生成时间无效："+mode);
        }
        var weekly=CronEditor.Preview("0 15 23 ? * MON,FRI","China Standard Time",anchor).Next;
        if(weekly.Any(x=>x.DayOfWeek is not (DayOfWeek.Monday or DayOfWeek.Friday)) || weekly.Any(x=>x.Hour!=23||x.Minute!=15))throw new Exception("星期或时间映射错误。");
        var monthly=CronEditor.Preview("0 0 12 31 * ?","UTC",new(2026,1,31,13,0,0,TimeSpan.Zero)).Next;
        if(monthly[0].Month!=3 || monthly.Any(x=>x.Day!=31))throw new Exception("每月31日未跳过缺失日期。");
        var leap=CronEditor.Preview("0 0 12 L * ?","UTC",new(2028,2,1,0,0,0,TimeSpan.Zero));
        if(!leap.Valid || leap.Next[0].Day!=29 || CronEditor.Read("0 0 12 L * ?").Frequency!=CronFrequency.Manual)throw new Exception("高级规则或闰月预览错误。");
        var local=CronEditor.Preview("0 0 12 * * ?","China Standard Time",anchor).Next[0];
        var utc=CronEditor.Preview("0 0 12 * * ?","UTC",anchor).Next[0];
        if(utc-local!=TimeSpan.FromHours(8))throw new Exception("时区未参与预览计算。");
        if(CronEditor.Preview("bad","UTC",anchor).Valid || CronEditor.Preview("0 0 12 * * ?","invalid-zone",anchor).Valid || CronEditor.Preview("0 61 12 * * ?","UTC",anchor).Valid)throw new Exception("非法表达式或时区未拒绝。");
        if(CronEditor.Read("0 0 12 ? * MON-FRI").Frequency!=CronFrequency.Manual)throw new Exception("复杂表达式被错误简化。");
        if(CronEditor.Read("0 * * * * ?") is not {Frequency:CronFrequency.Minutes,Interval:1})throw new Exception("每分钟计划未还原。");
        try{CronEditor.Generate(new(CronFrequency.Weekly,Weekdays:""));throw new Exception("未选择星期仍生成了计划。");}catch(ArgumentException){ }
        Console.WriteLine("PASS Cron：六种频率还原、Quartz未来时间、星期、月末/闰月、时区、高级规则及无效输入。");
    }
}
