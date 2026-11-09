using System.Globalization;
using Microsoft.Data.SqlClient;
using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Read-only SQL candle source matching orb-analysis/Services/MarketDataRepository.
/// Empty symbol (the default) automatically selects the most actively traded
/// MES contract for each Eastern calendar date, without mixing contract prices
/// within a day. A nonempty symbol reads only that exact value.
/// </summary>
public static class SqlMarketDataLoader
{
    public static async Task<List<PriceBar>> LoadAsync(
        string? connectionString, string symbol, string timestampTimeZone,
        CancellationToken cancellationToken = default)
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

        symbol = symbol.Trim();
        // Same table and source columns as orb-analysis. Include the *actual*
        // per-row symbol rather than assuming that SQL symbol equals "MES".
        const string sql = """
            SELECT ts_event, symbol, [open], high, low, [close], volume
            FROM dbo.MES_DATA
            WHERE (@symbol = '' OR symbol = @symbol)
            ORDER BY ts_event, symbol;
            """;

        var result = new List<PriceBar>();
        var dayBars = new List<PriceBar>();
        DateOnly? currentDay = null;
        string? lastSelectedSymbol = null;
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
                        $"Non-integer MES_DATA volume at {raw:yyyy-MM-dd HH:mm:ss}: {volumeText}");
                if (high < low || high < open || high < close ||
                    low > open || low > close)
                    throw new FormatException(
                        $"Invalid MES_DATA OHLC at {raw:yyyy-MM-dd HH:mm:ss}.");

                var candle = new Candle
                {
                    T = timestamp, O = open, H = high, L = low, C = close, V = volume
                };
                var bar = new PriceBar(timestamp, actualSymbol, candle);
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
            // A connected database can contain contract-specific symbols (MESU6,
            // MESZ6, etc.) rather than the literal symbol "MES".
            const string listSql = """
                SELECT TOP (20) symbol, COUNT_BIG(*) AS candle_count
                FROM dbo.MES_DATA
                GROUP BY symbol
                ORDER BY candle_count DESC, symbol;
                """;
            await using var list = new SqlCommand(listSql, connection);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            var available = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
                available.Add($"'{(reader.IsDBNull(0) ? "(NULL)" : reader.GetString(0))}' ({reader.GetInt64(1):N0} bars)");

            throw new InvalidOperationException(
                $"dbo.MES_DATA has no candles for symbol '{symbol}'. " +
                "Available database symbols: " +
                (available.Count == 0 ? "(table empty)" : string.Join(", ", available)) +
                ". Set MarketData:Symbol to an exact value, or set it to an empty string " +
                "to automatically choose the most active symbol each trading day.");
        }

        Console.WriteLine(
            symbol.Length == 0
                ? $"SQL auto-selected the most active symbol on {selectedDays:N0} calendar dates."
                : $"SQL selected exact symbol '{symbol}'.");
        return result;

        void FlushDay(DateOnly day)
        {
            if (dayBars.Count == 0) return;

            var groups = dayBars
                .GroupBy(bar => bar.Contract, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    Name = g.Key,
                    Bars = g.OrderBy(b => b.T).ToArray(),
                    RthBars = g.Count(b => IsRegularTradingHour(b.T)),
                    RthVolume = g.Where(b => IsRegularTradingHour(b.T))
                        .Sum(b => (decimal)Math.Max(0L, b.C.V)),
                    AllVolume = g.Sum(b => (decimal)Math.Max(0L, b.C.V))
                });
            var winner = groups
                .OrderByDescending(g => g.RthVolume)
                .ThenByDescending(g => g.RthBars)
                .ThenByDescending(g => g.AllVolume)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .First();

            if (symbol.Length == 0 && !string.Equals(
                    lastSelectedSymbol, winner.Name, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"SQL selected contract {winner.Name} beginning {day:yyyy-MM-dd} " +
                    $"(regular-session volume {winner.RthVolume:N0}).");
                lastSelectedSymbol = winner.Name;
            }

            DateTimeOffset? lastMinute = null;
            foreach (var bar in winner.Bars)
            {
                if (lastMinute is { } previous && bar.T <= previous)
                    throw new FormatException(
                        $"Duplicate or non-increasing MES_DATA minute for symbol " +
                        $"'{winner.Name}' at {bar.T:O}; check SQL table for duplicates.");
                result.Add(bar);
                lastMinute = bar.T;
            }

            selectedDays++;
        }

        static bool IsRegularTradingHour(DateTimeOffset timestamp)
        {
            var et = MarketTime.ToEastern(timestamp).TimeOfDay;
            return et >= new TimeSpan(9, 30, 0) && et < new TimeSpan(16, 0, 0);
        }
    }
}
