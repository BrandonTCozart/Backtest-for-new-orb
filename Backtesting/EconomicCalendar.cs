using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

// Scheduled US market catalysts for ES history spanning Oct 2025-Oct 2026. All times Eastern. Only the EVENT SCHEDULE is encoded, not
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
    private const string BlsHistorical = "https://www.bls.gov/schedule/2025/";
    private const string Bea = "https://www.bea.gov/news/schedule/full";
    private const string Fed = "https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm";
    private const string Ism = "https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/";
    private const string Retail = "https://www.census.gov/retail/release_schedule.html";

    // These dates are actual published release dates, including the late-2025
    // federal shutdown reschedules. No implied monthly dates are fabricated.
    // Verified from BLS 2025-2026, BEA 2026, Fed 2025-2026, ISM, Census.
    private static readonly EconomicEvent[] Scheduled = [
        E("2025-10-08", "14:00", "FOMC_MINUTES", "FOMC meeting minutes", Fed, false),
        E("2025-10-24", "08:30", "CPI", "Consumer Price Index", BlsHistorical),
        E("2025-10-29", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2025-11-03", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2025-11-05", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2025-11-19", "14:00", "FOMC_MINUTES", "FOMC meeting minutes", Fed, false),
        E("2025-11-20", "08:30", "NFP", "Employment Situation", BlsHistorical),
        E("2025-11-25", "08:30", "PPI", "Producer Price Index", BlsHistorical),
        E("2025-12-01", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2025-12-03", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2025-12-05", "10:00", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2025-12-09", "10:00", "JOLTS", "JOLTS job openings", BlsHistorical),
        E("2025-12-10", "08:30", "ECI", "Employment Cost Index", BlsHistorical),
        E("2025-12-10", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2025-12-16", "08:30", "NFP", "Employment Situation", BlsHistorical),
        E("2025-12-18", "08:30", "CPI", "Consumer Price Index", BlsHistorical),
        E("2025-12-23", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2025-12-30", "14:00", "FOMC_MINUTES", "FOMC meeting minutes", Fed, false),
        E("2026-01-05", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-01-07", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-01-07", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-01-09", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-01-13", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-01-14", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-01-14", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-01-22", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-01-22", "10:00", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-01-28", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2026-01-30", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-02-02", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-02-04", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-02-05", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-02-10", "08:30", "ECI", "Employment Cost Index", Bls),
        E("2026-02-10", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-02-11", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-02-13", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-02-18", "14:00", "FOMC_MINUTES", "FOMC meeting minutes", Fed, false),
        E("2026-02-20", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-02-20", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-02-27", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-03-02", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-03-04", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-03-06", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-03-06", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-03-11", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-03-13", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-03-13", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-03-13", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-03-18", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2026-03-18", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-03-31", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-04-01", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-04-01", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-04-03", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-04-06", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-04-08", "14:00", "FOMC_MINUTES", "FOMC meeting minutes", Fed, false),
        E("2026-04-09", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-04-09", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-04-10", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-04-14", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-04-21", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-04-29", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2026-04-30", "08:30", "ECI", "Employment Cost Index", Bls),
        E("2026-04-30", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-04-30", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-05-01", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-05-05", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-05-05", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-05-08", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-05-12", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-05-13", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-05-14", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-05-20", "14:00", "FOMC_MINUTES", "FOMC meeting minutes", Fed, false),
        E("2026-05-28", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-05-28", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-06-01", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-06-02", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-06-03", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-06-05", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-06-10", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-06-11", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-06-17", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2026-06-17", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-06-25", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-06-25", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-06-30", "10:00", "JOLTS", "JOLTS job openings", Bls),
        E("2026-07-01", "10:00", "ISM_MANUFACTURING", "ISM Manufacturing PMI", Ism),
        E("2026-07-02", "08:30", "NFP", "Employment Situation", Bls),
        E("2026-07-06", "10:00", "ISM_SERVICES", "ISM Services PMI", Ism),
        E("2026-07-08", "14:00", "FOMC_MINUTES", "FOMC meeting minutes", Fed, false),
        E("2026-07-14", "08:30", "CPI", "Consumer Price Index", Bls),
        E("2026-07-15", "08:30", "PPI", "Producer Price Index", Bls),
        E("2026-07-16", "08:30", "RETAIL_SALES", "Advance retail sales", Retail),
        E("2026-07-29", "14:00", "FOMC_DECISION", "FOMC rate decision", Fed),
        E("2026-07-30", "08:30", "GDP", "Gross Domestic Product", Bea),
        E("2026-07-30", "08:30", "PCE", "Personal Income and Outlays / PCE", Bea),
        E("2026-07-31", "08:30", "ECI", "Employment Cost Index", Bls),
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
        File.WriteAllLines(Path.Combine(folder, "optimizer_economic_calendar_2025_2026.csv"), rows);
    }

    private static EconomicEvent E(string date, string time, string code,
        string description, string source, bool major = true) =>
        new(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeSpan.ParseExact(time, @"hh\:mm", CultureInfo.InvariantCulture),
            code, description, source, major);
}
