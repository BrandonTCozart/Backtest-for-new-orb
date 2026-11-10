using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Intraday structure signals using only completed one-minute ES candles.
/// All highs/lows used as references are finalized strictly before the
/// signal candle; order entry remains at the following minute's open.
/// </summary>
public static class PriceActionSignals
{
    public readonly record struct Setup(
        int Index, TradeDirection Side, decimal Atr,
        decimal? TargetPrice = null);

    public static bool IsPriceAction(MultiStrategyKind kind) => kind is
        MultiStrategyKind.MorningSupportResistanceBounce or
        MultiStrategyKind.AfternoonSupportResistanceBounce or
        MultiStrategyKind.MorningSupportResistanceBreakout or
        MultiStrategyKind.AfternoonSupportResistanceBreakout or
        MultiStrategyKind.MorningBreakoutRetest or
        MultiStrategyKind.AfternoonBreakoutRetest or
        MultiStrategyKind.MorningFibonacciRetracement or
        MultiStrategyKind.AfternoonFibonacciRetracement or
        MultiStrategyKind.MorningFibonacciExtension or
        MultiStrategyKind.AfternoonFibonacciExtension or
        MultiStrategyKind.MorningLiquiditySweep or
        MultiStrategyKind.AfternoonLiquiditySweep;

    private static MultiStrategyKind Family(MultiStrategyKind kind) => kind switch
    {
        MultiStrategyKind.AfternoonSupportResistanceBounce => MultiStrategyKind.MorningSupportResistanceBounce,
        MultiStrategyKind.AfternoonSupportResistanceBreakout => MultiStrategyKind.MorningSupportResistanceBreakout,
        MultiStrategyKind.AfternoonBreakoutRetest => MultiStrategyKind.MorningBreakoutRetest,
        MultiStrategyKind.AfternoonFibonacciRetracement => MultiStrategyKind.MorningFibonacciRetracement,
        MultiStrategyKind.AfternoonFibonacciExtension => MultiStrategyKind.MorningFibonacciExtension,
        MultiStrategyKind.AfternoonLiquiditySweep => MultiStrategyKind.MorningLiquiditySweep,
        _ => kind
    };

    public static (int First, int LastSignal, int LastExit) Window(MultiStrategyVariant v) =>
        v.Slot == MultiStrategySlot.Afternoon
            ? (210 + v.StartShiftMinutes, 335, 384)
            : (15 + v.StartShiftMinutes, 105, 119);

    public static Setup? Find(MultiStrategyVariant v, MultiSession s)
    {
        if (!IsPriceAction(v.Kind) || v.Slot == MultiStrategySlot.Overnight)
            return null;

        var family = Family(v.Kind);
        var bars = s.Rth;
        var indicators = s.RthIndicators;
        var (first, end, _) = Window(v);
        for (var i = first; i <= end; i++)
        {
            if (i <= 1 || indicators.Atr14[i] <= 0m)
                continue;

            var atr = indicators.Atr14[i];
            var current = bars[i].C;
            var previous = bars[i - 1].C;
            TradeDirection? side = null;
            decimal? target = null;

            if (family is MultiStrategyKind.MorningFibonacciRetracement or
                          MultiStrategyKind.MorningFibonacciExtension)
            {
                // The lookback ends at i-1. Neither the signal candle's
                // extremes nor any future pivot determines Fibonacci anchors.
                if (i < v.Lookback) continue;
                var swing = FindCompletedSwing(bars, i, v.Lookback);
                if (swing is null || swing.Value.Size < atr * 2m)
                    continue;
                var (low, high, lowAt, highAt, _) = swing.Value;
                if (highAt > lowAt + 1)
                {
                    // Upward impulse from a prior low to a later prior high.
                    if (family == MultiStrategyKind.MorningFibonacciRetracement)
                    {
                        var level = high - (high - low) * v.FibRatio;
                        if (current.L <= level + v.Threshold &&
                            current.L >= low && current.C >= level &&
                            current.C < high && current.C > current.O)
                        {
                            side = TradeDirection.Long;
                            target = high; // Previous completed swing high.
                        }
                    }
                    else
                    {
                        var extension = low + (high - low) * v.FibRatio;
                        if (previous.C <= high + v.Threshold &&
                            current.C > high + v.Threshold &&
                            current.C > current.O && extension > current.C)
                        {
                            side = TradeDirection.Long;
                            target = extension;
                        }
                    }
                }
                else if (lowAt > highAt + 1)
                {
                    // Downward impulse from an earlier high to a later low.
                    if (family == MultiStrategyKind.MorningFibonacciRetracement)
                    {
                        var level = low + (high - low) * v.FibRatio;
                        if (current.H >= level - v.Threshold &&
                            current.H <= high && current.C <= level &&
                            current.C > low && current.C < current.O)
                        {
                            side = TradeDirection.Short;
                            target = low; // Previous completed swing low.
                        }
                    }
                    else
                    {
                        var extension = high - (high - low) * v.FibRatio;
                        if (previous.C >= low - v.Threshold &&
                            current.C < low - v.Threshold &&
                            current.C < current.O && extension < current.C)
                        {
                            side = TradeDirection.Short;
                            target = extension;
                        }
                    }
                }
            }
            else if (family == MultiStrategyKind.MorningBreakoutRetest)
            {
                side = FindRetest(v, s, i);
            }
            else
            {
                var levels = LevelsAt(v, s, i);
                if (levels is null) continue;
                var (support, resistance) = levels.Value;
                var buffer = v.Threshold;
                switch (family)
                {
                    case MultiStrategyKind.MorningSupportResistanceBounce:
                        if (current.L <= support + buffer &&
                            current.L >= support - buffer &&
                            current.C > support + buffer &&
                            current.C > current.O)
                            side = TradeDirection.Long;
                        else if (current.H >= resistance - buffer &&
                                 current.H <= resistance + buffer &&
                                 current.C < resistance - buffer &&
                                 current.C < current.O)
                            side = TradeDirection.Short;
                        break;

                    case MultiStrategyKind.MorningSupportResistanceBreakout:
                        if (previous.C <= resistance &&
                            current.C > resistance + buffer &&
                            current.C > current.O)
                            side = TradeDirection.Long;
                        else if (previous.C >= support &&
                                 current.C < support - buffer &&
                                 current.C < current.O)
                            side = TradeDirection.Short;
                        break;

                    case MultiStrategyKind.MorningLiquiditySweep:
                        if (previous.C >= support &&
                            current.L < support - buffer &&
                            current.C >= support + 0.25m &&
                            current.C > current.O)
                            side = TradeDirection.Long;
                        else if (previous.C <= resistance &&
                                 current.H > resistance + buffer &&
                                 current.C <= resistance - 0.25m &&
                                 current.C < current.O)
                            side = TradeDirection.Short;
                        break;
                }
            }

            if (side is not null)
                return new Setup(i, side.Value, atr, target);
        }
        return null;
    }

    // All reference levels are known before the signal's index. The previous
    // RTH reference is not continued across a contract rollover or a long gap.
    internal static (decimal Low, decimal High)? LevelsAt(
        MultiStrategyVariant v, MultiSession s, int index)
    {
        return v.LevelSource switch
        {
            PriceLevelSource.OpeningRange when index >= 15 =>
                (s.OpeningLow, s.OpeningHigh),
            PriceLevelSource.PreviousCompleteRth
                when s.PriorCompleteRthLow is decimal low &&
                     s.PriorCompleteRthHigh is decimal high =>
                (low, high),
            PriceLevelSource.Overnight when s.OvernightLow is decimal onLow &&
                s.OvernightHigh is decimal onHigh => (onLow, onHigh),
            PriceLevelSource.Rolling when index >= v.Lookback =>
                (s.Rth.Skip(index - v.Lookback).Take(v.Lookback).Min(x => x.C.L),
                 s.Rth.Skip(index - v.Lookback).Take(v.Lookback).Max(x => x.C.H)),
            _ => null
        };
    }

    // First breakout must be observable at candle j, and the retest is a
    // completed candle at i > j. For rolling levels the reference is frozen
    // at j, not recalculated using later bars.
    private static TradeDirection? FindRetest(
        MultiStrategyVariant v, MultiSession s, int i)
    {
        var bars = s.Rth;
        var tolerance = Math.Max(0.25m, v.Threshold);
        var earliest = Math.Max(15, i - v.RetestBars);
        for (var j = i - 1; j >= earliest; j--)
        {
            var levels = LevelsAt(v, s, j);
            if (levels is null) continue;
            var (support, resistance) = levels.Value;
            var breakout = bars[j].C;
            var before = bars[j - 1].C;
            var retest = bars[i].C;

            if (before.C <= resistance &&
                breakout.C > resistance + v.Threshold &&
                retest.L <= resistance + tolerance &&
                retest.C > resistance + tolerance / 2m &&
                retest.C > retest.O &&
                HeldLevel(bars, j + 1, i, resistance, TradeDirection.Long, tolerance))
                return TradeDirection.Long;

            if (before.C >= support &&
                breakout.C < support - v.Threshold &&
                retest.H >= support - tolerance &&
                retest.C < support - tolerance / 2m &&
                retest.C < retest.O &&
                HeldLevel(bars, j + 1, i, support, TradeDirection.Short, tolerance))
                return TradeDirection.Short;
        }
        return null;
    }

    private static bool HeldLevel(
        PriceBar[] bars, int start, int end, decimal level,
        TradeDirection side, decimal tolerance)
    {
        for (var i = start; i < end; i++)
        {
            var close = bars[i].C.C;
            if (side == TradeDirection.Long ? close < level - tolerance :
                close > level + tolerance)
                return false;
        }
        return true;
    }

    internal static (decimal Low, decimal High, int LowAt, int HighAt, decimal Size)?
        FindCompletedSwing(PriceBar[] bars, int signalIndex, int lookback)
    {
        if (lookback < 3 || signalIndex < lookback) return null;
        var start = signalIndex - lookback;
        decimal low = decimal.MaxValue, high = decimal.MinValue;
        int lowAt = -1, highAt = -1;
        for (var i = start; i < signalIndex; i++)
        {
            var c = bars[i].C;
            if (c.L < low) { low = c.L; lowAt = i; }
            if (c.H > high) { high = c.H; highAt = i; }
        }
        return (low, high, lowAt, highAt, high - low);
    }
}
