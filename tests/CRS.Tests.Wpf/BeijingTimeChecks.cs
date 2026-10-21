using System.Globalization;
using CRS.Plugin.Wpf;
using CRS.Timing;
using CRS.DesktopClient.ViewModels;
using CRS.Worker.Abstractions;

internal static class BeijingTimeChecks
{
    internal static void Run()
    {
        var converter = new BeijingTimestampConverter();
        var value = new DateTimeOffset(2026,10,22,0,30,0,BeijingClock.Offset);
        object Display(object input) => converter.Convert(input,typeof(string),null!,CultureInfo.InvariantCulture);
        if (!Equals(Display(value),"2026-10-22 00:30:00") || !Equals(Display(value.ToString("O")),"2026-10-22 00:30:00") || !Equals(Display(null!),""))
            throw new Exception("北京时间直接显示或空结束时间错误。");
        using var lifetime = new CancellationTokenSource();
        var model = new BackgroundServiceViewModel(lifetime.Token);
        var plan = new TaskDefinition("crs.clock","clock","时间验收","0 0 12 L * ?","UTC",true,1,false);
        var edit = new ScheduleEditViewModel(model,plan);
        if(edit.TimeZoneId!=BeijingClock.ZoneId || edit.Cron!=plan.Cron || !edit.IsManual || !edit.Valid || edit.NextTimes.Any(x=>!x.EndsWith("+08:00",StringComparison.Ordinal)))
            throw new Exception("计划没有统一北京时间或高级表达式被改写。");
        Console.WriteLine("PASS 北京时间界面：原值直接格式化、空时间、固定时区预览及高级Cron保留。");
    }
}
