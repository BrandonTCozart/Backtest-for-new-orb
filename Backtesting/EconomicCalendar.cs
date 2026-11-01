using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

// Verified scheduled US market catalysts for the requested Aug-Oct 2026 ES
// history. All times Eastern. Only the EVENT SCHEDULE is encoded, not
// unverified surprise estimates or revised macroeconomic values.
// Source calendars: bls.gov/schedule/2026/, bea.gov/news/schedule/full,
// federalreserve.gov/monetarypolicy/fomccalendars.htm,
// ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/
public enum EconomicEventRule
{
    Off = 0,
    AvoidMajorReleaseDay = 1,
    AvoidIntradayReleaseWindow = 2,
    AvoidFomcDayAndPriorTradingDay = 3
}

public sealed record EconomicEvent(
    DateOnly Day, TimeSpan TimeEt, string Code, string Description,
    string SourceUrl, bool Major);

public static class EconomicCalendar
{
    private const string Bls = "https://www.bls.gov/schedule/2026/";
    private const string Bea = "https://www.bea.gov/news/schedule/full";
    private const string Fed = "https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm";
    private const string Ism = "https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/";
    private const string Retail = "https://www.census.gov/retail/release_schedule.html";

    // All values below come from published release calendars (not hindsight
    // predictions). Dates are intentionally finite to this dataset's window.
    private static readonly EconomicEvent[] Scheduled = [
        E("2026-08-03", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-08-04", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-08-05", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-08-07", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-08-12", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-08-13", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-08-19", "14:00", "FOMC_MINUTES", "FOMC July meeting minutes", Fed, false),
        E("2026-08-26", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-08-26", "08:30", "GDP", "GDP second estimate", Bea),
        E("2026-09-01", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-09-01", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-09-03", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-09-04", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-09-10", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-09-11", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-09-16", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2026-09-29", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-09-30", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-09-30", "08:30", "GDP", "GDP third estimate", Bea),
        E("2026-10-01", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-10-02", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-10-05", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-10-07", "14:00", "FOMC_MINUTES", "FOMC September meeting minutes", Fed, false),
        E("2026-10-14", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-10-15", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-10-15", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-10-28", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2026-10-29", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-10-29", "08:30", "GDP", "GDP advance estimate", Bea),
        E("2026-10-30", "08:30", "ECI", "Employment Cost Index", Bls)
    ];

    private static readonly Dictionary<DateOnly, EconomicEvent[]> ByDay =
        Scheduled.GroupBy(e => e.Day).ToDictionary(x => x.Key, x => x.ToArray());

    public static IReadOnlyList<EconomicEvent> Events(DateOnly day) =>
        ByDay.TryGetValue(day, out var found) ? found : [];

    public static bool IsMajorEventDay(DateOnly day) => Events(day).Any(e => e.Major);

    public static bool IsFedDecisionDay(DateOnly day) =>
        Events(day).Any(e => e.Code == "FOMC_DECISION");

    // Uses day-before calendar relationship rather than future rate policy.
    public static bool IsBeforeFedDecision(DateOnly day) =>
        Scheduled.Any(e => e.Code == "FOMC_DECISION" &&
            PreviousTradingDay(e.Day) == day);

    public static int? CalendarDaysUntilNextFedDecision(DateOnly day)
    {
        var next = Scheduled.Where(e => e.Code == "FOMC_DECISION" && e.Day >= day)
            .OrderBy(e => e.Day).FirstOrDefault();
        return next is null ? null : next.Day.DayNumber - day.DayNumber;
    }

    // Each mode is deliberately simple and is tested as another walk-forward
    // training-only configuration. Modes use release schedules, never values
    // that would only become known after the simulated entry.
    public static bool AllowsEntry(EconomicEventRule rule, DateOnly day, DateTimeOffset entry)
    {
        return rule switch
        {
            EconomicEventRule.Off => true,
            EconomicEventRule.AvoidMajorReleaseDay => !IsMajorEventDay(day),
            EconomicEventRule.AvoidFomcDayAndPriorTradingDay =>
                !IsFedDecisionDay(day) && !IsBeforeFedDecision(day),
            EconomicEventRule.AvoidIntradayReleaseWindow => !Events(day)
                .Any(e => e.TimeEt >= new TimeSpan(9, 30, 0) &&
                    InReleaseWindow(MarketTime.ToEastern(entry).TimeOfDay, e.TimeEt)),
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
    }

    private static bool InReleaseWindow(TimeSpan entry, TimeSpan release) =>
        entry >= release - TimeSpan.FromMinutes(30) &&
        entry <= release + TimeSpan.FromMinutes(15);

    private static DateOnly PreviousTradingDay(DateOnly date)
    {
        var prev = date.AddDays(-1);
        while (prev.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            prev = prev.AddDays(-1);
        return prev;
    }

    public static string Codes(DateOnly day) =>
        string.Join("+", Events(day).Select(e => e.Code));

    public static void WriteCalendarCsv(string folder)
    {
        var rows = new List<string> {
            "date_et,release_time_et,event_code,event,major_event,source_url"
        };
        foreach (var e in Scheduled)
            rows.Add(string.Join(",",
                e.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                e.TimeEt.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                e.Code, e.Description, e.Major ? "yes" : "no", e.SourceUrl));
        File.WriteAllLines(Path.Combine(folder, "optimizer_economic_calendar_2026.csv"), rows);
    }

    private static EconomicEvent E(string date, string time, string code,
        string description, string source, bool major = true) =>
        new(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeSpan.ParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture),
            code, description, source, major);
}
