namespace Orb;

// Same timezone conversion as the live Orb worker; all CSV timestamps are UTC.
public static class MarketTime
{
    public static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Eastern), TimeSpan.Zero);
    }

    public static DateTimeOffset ToEastern(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, Eastern);
}
