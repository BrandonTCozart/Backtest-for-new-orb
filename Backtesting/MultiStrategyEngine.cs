using Orb;

namespace Backtest_for_new_orb;

public static class MultiStrategyEngine
{
    // Initial ES risk is capped at $600 including the existing $10/contract
    // estimated round-trip transaction cost.
    private const decimal Tick = 0.25m;
    private const decimal MaxInitialRiskDollars = 600m;

    public static IReadOnlyList<MultiStrategyVariant> EnumerateVariants()
    {
        var list = new List<MultiStrategyVariant>();
        var id = 0;
        // ORB is still the original strategy implementation, NOT a re-creation
        // using alternative exit rules. Compare against additional strategies.
        foreach (var cutoff in new[] { 30, 90 }) // 10:00 or 11:00 ET from 09:30
        foreach (var buffer in new[] { 0.5m, 1m })
        foreach (var volume in new[] { 0m, 1.25m })
        foreach (var trail in new[] { 150m, 350m })
        foreach (var rule in new[] { EconomicEventRule.Off,
                                    EconomicEventRule.AvoidFomcDayAndPriorTradingDay })
            list.Add(new MultiStrategyVariant(++id, MultiStrategyKind.Orb, 0, buffer,
                0m, 0m, volume, cutoff, trail, rule));

        foreach (var kind in Enum.GetValues<MultiStrategyKind>().Where(k => k != MultiStrategyKind.Orb))
        foreach (var lookback in new[] { 10, 20 })
        foreach (var threshold in kind is MultiStrategyKind.MorningMomentum or
                 MultiStrategyKind.AfternoonMomentum or MultiStrategyKind.OvernightMomentum
                 ? new[] { 0.25m, 0.5m } : new[] { 0.75m, 1.25m })
        foreach (var stopAtr in new[] { 1m, 1.5m })
        foreach (var rewardRisk in new[] { 1m, 1.75m })
        foreach (var rule in new[] { EconomicEventRule.Off,
                                    EconomicEventRule.AvoidFomcDayAndPriorTradingDay })
            list.Add(new MultiStrategyVariant(++id, kind, lookback, threshold,
                stopAtr, rewardRisk,
                kind is MultiStrategyKind.MorningMomentum or MultiStrategyKind.AfternoonMomentum
                    ? 1.25m : 0m,
                0, 0m, rule));

        return list;
    }

    public static IReadOnlyDictionary<DateOnly, MultiStrategyTrade> Generate(
        MultiStrategyVariant variant, IReadOnlyList<MultiSession> sessions,
        IReadOnlyList<SessionData> originalOrbSessions)
    {
        var result = new Dictionary<DateOnly, MultiStrategyTrade>();
        if (variant.Kind == MultiStrategyKind.Orb)
        {
            // Existing OrbStage stop engine and FVG rules, same as prior optimizer.
            var orbConfig = new OptimizationConfig(
                variant.Id, TimeSpan.FromMinutes(9 * 60 + 30 + variant.OrbCutoffMinutes),
                new RangeBand(0m, 10000m), VwapRule.Off,
                variant.Threshold, variant.VolumeMultiple, false, 1,
                new RiskBand(600m, 500m), variant.OrbTrailDollars, 0,
                WeekdaySelection.All, variant.EventRule);
            var simulated = OptimizationEngine.Evaluate(orbConfig, originalOrbSessions, keepTrades: true);
            foreach (var t in simulated.DetailedTrades ?? [])
            {
                // There are no MES reversals in the multi-strategy grid.
                if (t.IsMes) continue;
                result[t.Date] = new MultiStrategyTrade(t.Date, variant.Kind, variant.Id,
                    t.Contract, t.Direction, t.EntryAt, t.ExitAt,
                    t.Entry, t.Exit, t.Quantity * 600m,
                    t.ExitReason, t.Quantity);
            }
            return result;
        }

        foreach (var s in sessions)
        {
            var trade = GenerateDay(variant, s);
            if (trade is not null &&
                EconomicCalendar.AllowsEntry(variant.EventRule, s.Day, trade.EntryAt))
                result.Add(s.Day, trade);
        }
        return result;
    }

    public static MultiStrategyTrade? GenerateDay(MultiStrategyVariant v, MultiSession s)
    {
        var overnight = v.Slot == MultiStrategySlot.Overnight;
        var bars = overnight ? s.Overnight : s.Rth;
        var m = overnight ? s.OvernightIndicators : s.RthIndicators;
        if (bars is null || m is null) return null;

        // Signals are always made on COMPLETED one-minute bars; entry is
        // the next minute's OPEN. No future indicators enter the decision.
        var (first, lastSignal, lastExit) = v.Kind switch
        {
            MultiStrategyKind.MorningMomentum => (15, 90, 119),
            MultiStrategyKind.MorningVwapReversion => (30, 105, 119),
            MultiStrategyKind.AfternoonMomentum => (210, 330, 384),
            MultiStrategyKind.AfternoonVwapReversion => (210, 335, 384),
            MultiStrategyKind.OvernightMomentum => (60, 180, 839),
            MultiStrategyKind.OvernightReversion => (60, 180, 839),
            _ => throw new ArgumentOutOfRangeException(nameof(v.Kind))
        };

        var overnightHigh = overnight ? bars.Take(60).Max(b => b.C.H) : 0m;
        var overnightLow = overnight ? bars.Take(60).Min(b => b.C.L) : 0m;
        for (var i = first; i <= lastSignal; i++)
        {
            if (i < v.Lookback || m.Atr14[i] <= 0m) continue;
            var price = bars[i].C.C;
            var candle = bars[i].C;
            var atr = m.Atr14[i];
            TradeDirection? side = null;
            switch (v.Kind)
            {
                case MultiStrategyKind.MorningMomentum:
                case MultiStrategyKind.AfternoonMomentum:
                {
                    var high = bars.Skip(i - v.Lookback).Take(v.Lookback).Max(b => b.C.H);
                    var low = bars.Skip(i - v.Lookback).Take(v.Lookback).Min(b => b.C.L);
                    if (!VolumePass(bars, i, v.VolumeMultiple)) break;
                    if (price > high + v.Threshold && m.Ema9[i] > m.Ema21[i] &&
                        price > m.Vwap[i])
                        side = TradeDirection.Long;
                    else if (price < low - v.Threshold && m.Ema9[i] < m.Ema21[i] &&
                             price < m.Vwap[i])
                        side = TradeDirection.Short;
                    break;
                }
                case MultiStrategyKind.MorningVwapReversion:
                case MultiStrategyKind.AfternoonVwapReversion:
                {
                    var rsi = Rsi(bars, i, v.Lookback);
                    if (price > m.Vwap[i] + atr * v.Threshold &&
                        rsi >= 65m && candle.C < candle.O)
                        side = TradeDirection.Short;
                    else if (price < m.Vwap[i] - atr * v.Threshold &&
                             rsi <= 35m && candle.C > candle.O)
                        side = TradeDirection.Long;
                    break;
                }
                case MultiStrategyKind.OvernightMomentum:
                    if (price > overnightHigh + atr * v.Threshold &&
                        m.Ema9[i] > m.Ema21[i])
                        side = TradeDirection.Long;
                    else if (price < overnightLow - atr * v.Threshold &&
                             m.Ema9[i] < m.Ema21[i])
                        side = TradeDirection.Short;
                    break;
                case MultiStrategyKind.OvernightReversion:
                {
                    var rsi = Rsi(bars, i, v.Lookback);
                    if (price > overnightHigh + atr * v.Threshold &&
                        rsi >= 65m && candle.C < candle.O)
                        side = TradeDirection.Short;
                    else if (price < overnightLow - atr * v.Threshold &&
                             rsi <= 35m && candle.C > candle.O)
                        side = TradeDirection.Long;
                    break;
                }
            }

            if (side is null) continue;
            return SimulateBracket(v, s, bars, i + 1, lastExit,
                side.Value, atr);
        }
        return null;
    }

    private static MultiStrategyTrade? SimulateBracket(
        MultiStrategyVariant v, MultiSession s, PriceBar[] bars, int entryIndex,
        int lastExit, TradeDirection side, decimal signalAtr)
    {
        if (entryIndex >= lastExit || lastExit >= bars.Length) return null;
        var entryAt = bars[entryIndex].T;
        var entry = bars[entryIndex].C.O;
        // Floor to a valid 0.25-point ES price increment, without exceeding
        // the specified $600 maximum ES initial risk including costs.
        var stopPoints = Math.Floor(
            Math.Min((MaxInitialRiskDollars - 10m) / 50m,
                signalAtr * v.StopAtrMultiple) / Tick) * Tick;
        if (stopPoints < 0.5m) return null;
        var targetPoints = Math.Floor(stopPoints * v.RewardRisk / Tick) * Tick;
        if (targetPoints < Tick) return null;
        var stop = side == TradeDirection.Long ? entry - stopPoints : entry + stopPoints;
        var target = side == TradeDirection.Long ? entry + targetPoints : entry - targetPoints;

        // Require contiguous minute data across the entire hypothetical
        // holding period; never interpolate a missing bar or move across it.
        for (var j = entryIndex + 1; j <= lastExit; j++)
            if (bars[j].T != bars[j - 1].T.AddMinutes(1))
                return null;

        for (var j = entryIndex; j <= lastExit; j++)
        {
            var c = bars[j].C;
            var stopped = side == TradeDirection.Long ? c.L <= stop : c.H >= stop;
            if (stopped)
            {
                // Conservative ordering when both stop and target are within
                // the same minute. A stop gap gets the worse opening fill.
                var fill = side == TradeDirection.Long
                    ? Math.Min(c.O, stop) : Math.Max(c.O, stop);
                return Create("stop", j, fill);
            }

            var targetHit = side == TradeDirection.Long ? c.H >= target : c.L <= target;
            if (targetHit) return Create("target", j, target);
            if (j == lastExit) return Create("time_exit", j, c.C);
        }
        return null;

        MultiStrategyTrade Create(string reason, int exitIndex, decimal fill) =>
            new(s.Day, v.Kind, v.Id, s.Contract, side,
                entryAt, bars[exitIndex].T.AddMinutes(1), entry, fill,
                stopPoints * 50m + 10m, reason);
    }

    private static bool VolumePass(PriceBar[] bars, int i, decimal multiple)
    {
        if (multiple <= 0m) return true;
        if (i < 10) return false;
        decimal sum = 0m;
        for (var j = i - 10; j < i; j++)
            sum += Math.Max(0L, bars[j].C.V);
        return sum > 0m && bars[i].C.V >= (sum / 10m) * multiple;
    }

    private static decimal Rsi(PriceBar[] bars, int index, int periods)
    {
        if (index < periods) return 50m;
        decimal gains = 0m, losses = 0m;
        for (var i = index - periods + 1; i <= index; i++)
        {
            var delta = bars[i].C.C - bars[i - 1].C.C;
            if (delta >= 0m) gains += delta;
            else losses -= delta;
        }
        if (gains + losses == 0m) return 50m;
        return 100m * gains / (gains + losses);
    }
}
