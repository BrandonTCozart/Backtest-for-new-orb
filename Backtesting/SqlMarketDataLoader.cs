using System.Globalization;
using Microsoft.Data.SqlClient;
using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Read-only SQL candle source matching orb-analysis/Services/MarketDataRepository.
/// The source table is named MES_DATA but contains ES contracts.
/// With an empty symbol, select the active ES contract from the most recently
/// COMPLETED regular session, never from the eventual volume of the trade day.
/// All raw contract bars remain available for causal lookbacks.
/// </summary>
public sealed record SqlMarketDataSet(List<PriceBar> ActiveBars, List<PriceBar> AllContractBars);

public static class SqlMarketDataLoader
{
    // SQL identifiers cannot be parameterized. Accept only these known tables
    // to prevent accidental writes or unsafe identifier interpolation.
    internal static string ValidateTableName(string tableName) =>
        tableName switch
        {
            "MES_DATA" => "MES_DATA",
            "MES_DATA_2020_2025" => "MES_DATA_2020_2025",
            _ => throw new ArgumentException(
                "SQL source must be MES_DATA or MES_DATA_2020_2025.", nameof(tableName))
        };

    public static async Task<SqlMarketDataSet> LoadAsync(
        string? connectionString, string symbol, string timestampTimeZone,
        CancellationToken cancellationToken = default,
        string tableName = "MES_DATA")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "SQL connection string missing. Set ConnectionStrings:MarketData in " +
                "appsettings.Development.json, .NET User Secrets, or the " +
                "ConnectionStrings__MarketData environment variable.");

        var isUtc = timestampTimeZone.Equals("UTC", StringComparison.OrdinalIgnoreCase);
        if (!isUtc && !timestampTimeZone.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "MarketData:TimestampTimeZone must be America/New_York or UTC.");

        tableName = ValidateTableName(tableName);
        symbol = symbol.Trim();
        // Same table and source columns as orb-analysis. Include the *actual*
        // per-row symbol rather than assuming that SQL symbol equals "MES".
        var sql = $"""
            SELECT ts_event, symbol, [open], high, low, [close], volume
            FROM dbo.[{tableName}]
            WHERE (@symbol = '' OR symbol = @symbol)
            ORDER BY ts_event, symbol;
            """;

        var result = new List<PriceBar>();
        var allContractBars = new List<PriceBar>();
        var dayBars = new List<PriceBar>();
        DateOnly? currentDay = null;
        var selector = new CausalContractSelector();
        var selectedDays = 0;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
        {
            command.Parameters.AddWithValue("@symbol", symbol);
            await using var reader = await command.ExecuteReaderAsync(
                System.Data.CommandBehavior.SequentialAccess, cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var raw = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Unspecified);
                var actualSymbol = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();
                // orb-analysis treats ts_event as a wall-clock timestamp. The
                // configurable interpretation prevents a shifted ORB window.
                var timestamp = isUtc
                    ? new DateTimeOffset(DateTime.SpecifyKind(raw, DateTimeKind.Utc))
                    : new DateTimeOffset(
                        TimeZoneInfo.ConvertTimeToUtc(raw, MarketTime.Eastern),
                        TimeSpan.Zero);
                var open = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
                var high = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
                var low = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
                var close = Convert.ToDecimal(reader.GetValue(5), CultureInfo.InvariantCulture);
                var volumeText = Convert.ToString(reader.GetValue(6), CultureInfo.InvariantCulture);
                if (!long.TryParse(volumeText, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var volume))
                    throw new FormatException(
                        $"Non-integer {tableName} volume at {raw:yyyy-MM-dd HH:mm:ss}: {volumeText}");
                if (high < low || high < open || high < close ||
                    low > open || low > close)
                    throw new FormatException(
                        $"Invalid {tableName} OHLC at {raw:yyyy-MM-dd HH:mm:ss}.");

                var candle = new Candle
                {
                    T = timestamp, O = open, H = high, L = low, C = close, V = volume
                };
                var bar = new PriceBar(timestamp, actualSymbol, candle);
                allContractBars.Add(bar);
                var day = DateOnly.FromDateTime(MarketTime.ToEastern(timestamp).Date);

                if (currentDay is { } previousDay && day != previousDay)
                {
                    FlushDay(previousDay);
                    dayBars.Clear();
                }

                currentDay = day;
                dayBars.Add(bar);
            }
        }

        if (currentDay is { } finalDay)
            FlushDay(finalDay);

        if (result.Count == 0)
        {
            // The mislabeled MES_DATA table contains contract-specific ES symbols.
            var listSql = $"""
                SELECT TOP (20) symbol, COUNT_BIG(*) AS candle_count
                FROM dbo.[{tableName}]
                GROUP BY symbol
                ORDER BY candle_count DESC, symbol;
                """;
            await using var list = new SqlCommand(listSql, connection);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            var available = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
                available.Add($"'{(reader.IsDBNull(0) ? "(NULL)" : reader.GetString(0))}' ({reader.GetInt64(1):N0} bars)");

            throw new InvalidOperationException(
                $"dbo.{tableName} has no candles for symbol '{symbol}'. " +
                "Available database symbols: " +
                (available.Count == 0 ? "(table empty)" : string.Join(", ", available)) +
                ". Set MarketData:Symbol to an exact value, or set it to an empty string " +
                "to automatically choose the most active symbol each trading day.");
        }

        Console.WriteLine(
            symbol.Length == 0
                ? $"SQL causally selected ES contracts from completed RTH volume on {selectedDays:N0} calendar dates (no same-day hindsight); " +
                  $"skipped {selector.BootstrapDays:N0} bootstrap/stale-source days."
                : $"SQL selected exact ES contract '{symbol}'.");
        Console.WriteLine($"SQL source rows (all contracts): {allContractBars.Count:N0}; " +
                          $"active-contract candle rows: {result.Count:N0}; " +
                          $"overlap/alternate-contract rows retained for historical lookback: {allContractBars.Count - result.Count:N0}");
        return new SqlMarketDataSet(result, allContractBars);

        void FlushDay(DateOnly day)
        {
            if (dayBars.Count == 0) return;
            if (symbol.Length == 0)
            {
                if (selector.SelectDay(day, dayBars, result)) selectedDays++;
                return;
            }

            DateTimeOffset? last = null;
            foreach (var bar in dayBars.Where(b =>
                         string.Equals(b.Contract, symbol, StringComparison.OrdinalIgnoreCase)))
            {
                if (last is not null && bar.T <= last.Value)
                    throw new FormatException("Duplicate or decreasing minute for " +
                        symbol + " at " + bar.T);
                result.Add(bar);
                last = bar.T;
            }
            if (last is not null) selectedDays++;
        }

    }

    private static bool IsRegularTradingHour(DateTimeOffset timestamp)
    {
        var et = MarketTime.ToEastern(timestamp).TimeOfDay;
        return et >= new TimeSpan(9, 30, 0) && et < new TimeSpan(16, 0, 0);
    }

    // A calendar day can straddle TWO trading sessions. Bars before 16:00
    // belong to the prior completed-session contract; bars from 18:00
    // belong to the NEXT session and may use today's 16:00 volume summary.
    // Never silently switch to whichever contract later had more activity.
    internal sealed class CausalContractSelector
    {
        private string? _selectedContract;
        private DateOnly? _referenceDay;
        internal int BootstrapDays { get; private set; }

        internal bool SelectDay(DateOnly day, IReadOnlyList<PriceBar> bars,
            List<PriceBar> output)
        {
            var groups = bars.GroupBy(b => b.Contract,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.T).ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            // Today's pre-16:00 bars use ONLY the past full RTH day's
            // selected contract. If unavailable, skip rather than peek at
            // the later today's volume or first future signal.
            var before = _referenceDay is { } prior &&
                         day.DayNumber - prior.DayNumber <= 7
                ? _selectedContract : null;

            // After 16:00 ET, today's completed RTH is already known and may
            // set the next (18:00 ET onward) session's contract.
            var current = groups.Select(pair =>
            {
                var rth = pair.Value.Where(b =>
                    IsRegularTradingHour(b.T)).ToArray();
                var start = MarketTime.ToUtc(day, new TimeOnly(9, 30));
                var valid = rth.Length == 390 &&
                    rth[0].T == start &&
                    rth[^1].T == start.AddMinutes(389) &&
                    rth.Zip(rth.Skip(1), (a, b) =>
                        b.T == a.T.AddMinutes(1)).All(x => x);
                return (Contract: pair.Key, Valid: valid,
                    RthVolume: rth.Sum(b => (decimal)Math.Max(0L, b.C.V)));
            }).Where(x => x.Valid)
              .OrderByDescending(x => x.RthVolume)
              .ThenBy(x => x.Contract, StringComparer.OrdinalIgnoreCase)
              .FirstOrDefault();

            var after = current.Valid ? current.Contract : before;
            if (current.Valid)
            {
                if (!string.Equals(_selectedContract, after,
                        StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine("SQL causal contract roll: " + after +
                        " from " + day + " completed 16:00 ET RTH volume; " +
                        "effective from 18:00 ET.");
                _selectedContract = after;
                _referenceDay = day;
            }

            if (before is null) BootstrapDays++;
            var preCount = output.Count;
            foreach (var bar in bars)
            {
                var t = MarketTime.ToEastern(bar.T).TimeOfDay;
                var candidate = t >= new TimeSpan(18, 0, 0) ? after : before;
                if (candidate is not null &&
                    string.Equals(bar.Contract, candidate,
                        StringComparison.OrdinalIgnoreCase))
                    output.Add(bar);
            }
            // SQL is ordered by ts_event,symbol, so the selected subset
            // must remain strictly increasing and duplicate-free.
            DateTimeOffset? last = null;
            for (var i = preCount; i < output.Count; i++)
            {
                if (last is not null && output[i].T <= last.Value)
                    throw new FormatException("Duplicate selected minute at " +
                        output[i].T + "; check SQL contract bars.");
                last = output[i].T;
            }
            return output.Count > preCount;
        }
    }

}
