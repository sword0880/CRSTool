using System.Reflection;
using System.Text.Json;
using CRS.Timing;

internal static class CommonTimeCompatibilityChecks
{
    private static void Check(bool passed,string description)
    {
        if(!passed)throw new InvalidOperationException(description);
        Console.WriteLine("PASS "+description);
    }

    public static void Run()
    {
        // The new runtime owner must be CRS.Common; keeping the namespace means existing source APIs do not change.
        Check(typeof(BeijingClock).Assembly.GetName().Name=="CRS.Common","BeijingClock implementation is owned by CRS.Common");
        Check(typeof(BeijingDateTimeOffsetConverter).Assembly.GetName().Name=="CRS.Common","JSON time converter is owned by CRS.Common");

        // The old assembly remains loadable by previously compiled plugins and forwards BOTH public types.
        var compatibility=Assembly.Load(new AssemblyName("CRS.Time"));
        Check(compatibility.GetName().Name=="CRS.Time","legacy CRS.Time assembly identity is retained");
        var forwarded=compatibility.GetForwardedTypes().Select(type=>type.FullName).ToHashSet(StringComparer.Ordinal);
        Check(forwarded.Contains("CRS.Timing.BeijingClock")&&forwarded.Contains("CRS.Timing.BeijingDateTimeOffsetConverter"),
            "legacy CRS.Time forwards both public types without creating duplicate implementations");

        var utc=new DateTimeOffset(2026,10,26,16,35,0,TimeSpan.Zero);
        var beijing=BeijingClock.Normalize(utc);
        Check(beijing.Offset==TimeSpan.FromHours(8)&&beijing.Year==2026&&beijing.Month==10&&beijing.Day==27
            &&beijing.Hour==0&&beijing.Minute==35,"UTC instant crossing Beijing midnight retains calendar day and offset");
        Check(beijing.ToUniversalTime()==utc,"Beijing conversion preserves instant");
        Check(BeijingClock.At(new DateOnly(2026,10,27),new TimeOnly(0,35))==beijing,"Beijing local construction is stable");

        var options=new JsonSerializerOptions();
        options.Converters.Add(new BeijingDateTimeOffsetConverter());
        var json=JsonSerializer.Serialize(utc,options);
        Check(json.Contains("2026-10-27T00:35:00+08:00",StringComparison.Ordinal),
            "JSON time conversion writes the stable Beijing offset instead of machine-local offset");
        var oldPayload="\"2026-10-26T16:35:00+00:00\"";
        var decoded=JsonSerializer.Deserialize<DateTimeOffset>(oldPayload,options);
        Check(decoded==beijing&&decoded.Offset==TimeSpan.FromHours(8),"legacy explicit UTC offset reads into Beijing time without changing instant");
        Check(BeijingClock.Serialize(decoded)==BeijingClock.Serialize(utc),"time serialization normalizes the same instant identically");
    }
}
