namespace Backtest_for_new_orb;

// Bitmask for weekdays in exchange-local Eastern time:
// Monday=1, Tuesday=2, Wednesday=4, Thursday=8, Friday=16.
// Every non-empty subset is evaluated, including all five weekdays.
public static class WeekdaySelection
{
    public const int All = 31;

    public static bool Includes(int mask, DayOfWeek weekday)
    {
        int bit = weekday switch
        {
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 4,
            DayOfWeek.Thursday => 8,
            DayOfWeek.Friday => 16,
            _ => 0
        };
        return bit != 0 && (mask & bit) != 0;
    }

    public static string Format(int mask)
    {
        if (mask is < 1 or > All)
            throw new ArgumentOutOfRangeException(nameof(mask), "Expected a non-empty Mon-Fri selection.");
        string[] names = ["Mon", "Tue", "Wed", "Thu", "Fri"];
        return string.Join("+", Enumerable.Range(0, 5)
            .Where(i => (mask & (1 << i)) != 0)
            .Select(i => names[i]));
    }
}
