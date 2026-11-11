using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// A market regime is observed BEFORE the slot may submit its first order:
/// overnight at 19:00 ET, morning at 09:45 ET, afternoon at 13:00 ET.
/// References are earlier complete sessions of the SAME ES contract.
/// </summary>
public enum MarketRegime
{
    Trend, Range, Uncertain, VolatilityShock, ThinLiquidity, NoReference
}

public sealed record RegimeObservation(
    MultiStrategySlot Slot, DateTimeOffset AvailableAt, bool HasReference,
    decimal StrengthAtr, decimal Efficiency, decimal VolatilityRatio,
    decimal RelativeVolume);

public sealed record RegimeThresholds(
    decimal TrendStrengthAtr = 1.5m,
    decimal MinEfficiency = 0.2m,
    decimal MaxVolatilityRatio = 2m,
    decimal MinRelativeVolume = 0.6m);

public static class MarketRegimeEngine
{
    // Only 15 morning or 60 overnight/afternoon FULLY COMPLETED candles.
    // 'end' is an inclusive candle index, 'AvailableAt' is the next minute.
    public static RegimeObservation? Observe(
        MultiStrategySlot slot, PriceBar[]? bars, MultiIndicators? indicators,
        PriceBar[]? previousBars, MultiIndicators? previousIndicators)
    {
        var (start, end) = slot switch
        {
            MultiStrategySlot.Overnight => (0, 59),
            MultiStrategySlot.Morning => (0, 14),
            MultiStrategySlot.Afternoon => (150, 209),
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
        if (bars is null || indicators is null || bars.Length <= end)
            return null;

        var readyAt = bars[end].T.AddMinutes(1);
        var atr = indicators.Atr14[end];
        decimal distance = 0m, currentVolume = 0m;
        for (var i = start; i <= end; i++)
        {
            currentVolume += Math.Max(0L, bars[i].C.V);
            distance += Math.Abs(bars[i].C.C -
                (i == start ? bars[i].C.O : bars[i - 1].C.C));
        }
        var move = Math.Abs(bars[end].C.C - bars[start].C.O);
        var strength = atr > 0m ? move / atr : 0m;
        var efficiency = distance > 0m ? move / distance : 0m;

        if (previousBars is null || previousIndicators is null ||
            previousBars.Length <= end || atr <= 0m ||
            previousIndicators.Atr14[end] <= 0m)
            return new RegimeObservation(slot, readyAt, false,
                strength, efficiency, 0m, 0m);

        decimal previousVolume = 0m;
        for (var i = start; i <= end; i++)
            previousVolume += Math.Max(0L, previousBars[i].C.V);
        if (previousVolume <= 0m)
            return new RegimeObservation(slot, readyAt, false,
                strength, efficiency, 0m, 0m);

        return new RegimeObservation(slot, readyAt, true,
            strength, efficiency,
            atr / previousIndicators.Atr14[end],
            currentVolume / previousVolume);
    }

    // Deterministic, transparent, non-ML market classification. Thresholds
    // may be fitted only on earlier training dates and then frozen.
    public static MarketRegime Classify(
        RegimeObservation? observation, RegimeThresholds thresholds)
    {
        if (observation is null || !observation.HasReference)
            return MarketRegime.NoReference;
        if (observation.VolatilityRatio >= thresholds.MaxVolatilityRatio)
            return MarketRegime.VolatilityShock;
        if (observation.RelativeVolume < thresholds.MinRelativeVolume)
            return MarketRegime.ThinLiquidity;
        if (observation.StrengthAtr >= thresholds.TrendStrengthAtr &&
            observation.Efficiency >= thresholds.MinEfficiency)
            return MarketRegime.Trend;
        if (observation.StrengthAtr <= 0.75m * thresholds.TrendStrengthAtr ||
            observation.Efficiency <= thresholds.MinEfficiency * 0.5m)
            return MarketRegime.Range;
        return MarketRegime.Uncertain;
    }

    public static RegimeObservation? ForSlot(
        MultiSession day, MultiStrategySlot slot) => slot switch
        {
            MultiStrategySlot.Overnight => day.OvernightRegime,
            MultiStrategySlot.Morning => day.MorningRegime,
            MultiStrategySlot.Afternoon => day.AfternoonRegime,
            _ => null
        };
}
