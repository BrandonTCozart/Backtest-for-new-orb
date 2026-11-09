using System.Globalization;
using Microsoft.Data.SqlClient;
using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Read-only candle loader for the SQL Server table already used by orb-analysis.
/// Produces the existing PriceBar/Candle input so optimization logic is unchanged.
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

        // Same source and symbol filter as orb-analysis/Services/MarketDataRepository.cs.
        const string sql = """
            SELECT ts_event, [open], high, low, [close], volume
            FROM dbo.MES_DATA
            WHERE (@symbol = '' OR symbol = @symbol)
            ORDER BY ts_event;
            """;

        var result = new List<PriceBar>();
        await using var connection = new SqlConnection(connectionString);
        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 0  // A complete historical scan may take longer than 30 seconds.
        };
        command.Parameters.AddWithValue("@symbol", symbol);
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(
            System.Data.CommandBehavior.SequentialAccess, cancellationToken);

        DateTimeOffset? previousTimestamp = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            var raw = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Unspecified);
            // orb-analysis works with the stored ts_event wall-clock timestamps
            // directly (e.g. 09:30 means 09:30 ET). Convert to UTC to preserve
            // the existing backtest's Eastern market-hours calculations.
            var timestamp = isUtc
                ? new DateTimeOffset(DateTime.SpecifyKind(raw, DateTimeKind.Utc))
                : new DateTimeOffset(
                    TimeZoneInfo.ConvertTimeToUtc(raw, MarketTime.Eastern),
                    TimeSpan.Zero);

            var open = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            var high = Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture);
            var low = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
            var close = Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
            var volumeText = Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture);
            if (!long.TryParse(volumeText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var volume))
                throw new FormatException(
                    $"Non-integer MES_DATA volume at {raw:yyyy-MM-dd HH:mm:ss}: {volumeText}");

            if (high < low || high < open || high < close ||
                low > open || low > close)
                throw new FormatException(
                    $"Invalid MES_DATA OHLC at {raw:yyyy-MM-dd HH:mm:ss}.");

            if (previousTimestamp is { } previous && timestamp <= previous)
                throw new FormatException(
                    $"MES_DATA timestamps must be unique and increasing for symbol '{symbol}'. " +
                    $"Found {timestamp:O} after {previous:O}. " +
                    "Use a specific symbol if the table mixes contracts.");

            var candle = new Candle
            {
                T = timestamp, O = open, H = high, L = low, C = close, V = volume
            };
            result.Add(new PriceBar(timestamp, symbol, candle));
            previousTimestamp = timestamp;
        }

        if (result.Count == 0)
            throw new InvalidOperationException(
                $"dbo.MES_DATA has no candles for symbol '{symbol}'. " +
                "Check the MarketData:Symbol setting (empty string reads all symbols).");

        return result;
    }
}
