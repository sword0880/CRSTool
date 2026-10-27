using System.Text.Json;
using CRS.Timing;

internal static class CommonTimeChecks
{
    private static void Check(bool passed,string description)
    {
        if(!passed)throw new InvalidOperationException(description);
        Console.WriteLine("PASS "+description);
    }

    public static void Run()
    {
        // 时间实现只归属公共工程；保留时区语义与跨日、序列化检查。
        Check(typeof(BeijingClock).Assembly.GetName().Name=="CRS.Common","BeijingClock implementation is owned by CRS.Common");
        Check(typeof(BeijingDateTimeOffsetConverter).Assembly.GetName().Name=="CRS.Common","JSON time converter is owned by CRS.Common");
        Check(typeof(BeijingTime).Assembly.GetName().Name=="CRS.Common","Display formatter is owned by CRS.Common without WPF");
        Check(Equals(BeijingTime.Display(null),"")&&Equals(BeijingTime.Display("2026-10-27 12:34:56"),"2026-10-27 12:34:56")
            &&BeijingTime.Display(new DateTime(2026,10,27,12,34,56,DateTimeKind.Unspecified)) is DateTime,
            "Missing offset remains explicit and is not guessed during display");
        Check(Equals(BeijingTime.Display(new DateTimeOffset(2026,10,27,0,35,0,BeijingClock.Offset)),"2026-10-27 00:35:00"),
            "Already normalized Beijing values are formatted directly");

        var utc=new DateTimeOffset(2026,10,26,16,35,0,TimeSpan.Zero);
        var beijing=BeijingClock.Normalize(utc);
        Check(beijing.Offset==TimeSpan.FromHours(8)&&beijing.Year==2026&&beijing.Month==10&&beijing.Day==27
            &&beijing.Hour==0&&beijing.Minute==35,"UTC instant crossing Beijing midnight retains calendar day and offset");
        Check(beijing.ToUniversalTime()==utc,"Beijing conversion preserves instant");
        Check(BeijingClock.At(new DateOnly(2026,10,27),new TimeOnly(0,35))==beijing,"Beijing local construction is stable");

        var options=new JsonSerializerOptions();
        options.Converters.Add(new BeijingDateTimeOffsetConverter());
        var json=JsonSerializer.Serialize(utc,options);
        Check((JsonSerializer.Deserialize<string>(json)??string.Empty).Contains("2026-10-27T00:35:00+08:00",StringComparison.Ordinal),
            "JSON time conversion writes the stable Beijing offset instead of machine-local offset");
        var utcPayload="\"2026-10-26T16:35:00+00:00\"";
        var decoded=JsonSerializer.Deserialize<DateTimeOffset>(utcPayload,options);
        Check(decoded==beijing&&decoded.Offset==TimeSpan.FromHours(8),"explicit UTC offset reads into Beijing time without changing instant");
        Check(BeijingClock.Serialize(decoded)==BeijingClock.Serialize(utc),"time serialization normalizes the same instant identically");
    }
}
