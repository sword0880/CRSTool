using System.Globalization;

namespace CRS.Plugins.NationalTeamWatch.Domain;

public enum EtfPublicationPrecision { ExactTime=0, DateOnly=1 }

/// <summary>公告日期和准确时刻分别保存；系统首见证明何时取得，不改写为公告时刻。</summary>
public static class EtfHoldingPublication
{
    public static bool HasValidShape(DateTimeOffset? time,DateOnly? date,EtfPublicationPrecision precision)=>precision switch
    {
        EtfPublicationPrecision.ExactTime=>time is {} moment&&moment!=default&&date is null,
        EtfPublicationPrecision.DateOnly=>time is null&&date is {} day&&day!=default,
        _=>false
    };
    public static DateOnly? DeclaredDate(DateTimeOffset? time,DateOnly? date,EtfPublicationPrecision precision)=>
        !HasValidShape(time,date,precision)?null:precision==EtfPublicationPrecision.DateOnly?date:BeijingDay(time!.Value);

    public static bool IsValid(NamedEtfHolding value)
    {
        var date=DeclaredDate(value.PublishedAt,value.PublicationDate,value.PublicationPrecision);var seen=BeijingDay(value.FirstSeenAt);
        return value.FirstSeenAt!=default&&value.HoldingAsOf!=default&&date is {} declared&&seen is {} observed&&value.HoldingAsOf<=declared&&declared<=observed
            &&(value.PublicationPrecision!=EtfPublicationPrecision.ExactTime||value.PublishedAt<=value.FirstSeenAt);
    }
    /// <summary>这里只按已声明时序排除未来资料；缺字段的最新期留给核验失败关闭，不能回退旧期。</summary>
    public static bool KnownAfter(NamedEtfHolding value,DateTimeOffset cutoff)=>value.FirstSeenAt>cutoff
        ||value.PublicationPrecision==EtfPublicationPrecision.ExactTime&&value.PublishedAt>cutoff
        ||value.PublicationPrecision==EtfPublicationPrecision.DateOnly&&value.PublicationDate is {} date&&(BeijingDay(cutoff) is not {} day||date>day);

    public static string Describe(NamedEtfHolding value)=>DeclaredDate(value.PublishedAt,value.PublicationDate,value.PublicationPrecision) is null?"公布时间未核实":
        value.PublicationPrecision==EtfPublicationPrecision.DateOnly?value.PublicationDate!.Value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+"（仅日期，时刻未知）":
        value.PublishedAt!.Value.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture);
    private static DateOnly? BeijingDay(DateTimeOffset value)
    {try{return DateOnly.FromDateTime(value.ToOffset(TimeSpan.FromHours(8)).DateTime);}catch(ArgumentOutOfRangeException){return null;}}
}
