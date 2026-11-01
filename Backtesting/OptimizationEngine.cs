using Orb;

namespace Backtest_for_new_orb;

public static class OptimizationEngine
{
    private static readonly TimeSpan OrbEnd = new(9, 45, 0);
    private static readonly TimeSpan End = new(16, 0, 0);
    private static readonly TimeSpan Stage6Cutoff = new(13, 0, 0);
    private readonly record struct TradeKey(
        DateOnly Day, int SignalIndex, TradeDirection Side, int Quantity,
        bool Mes, decimal RiskCap, decimal Fallback, decimal EsTrail);
    private static readonly Dictionary<TradeKey, SimulatedTrade?> TradeCache = new();

    public static OptimizationResult Evaluate(
        OptimizationConfig config, IReadOnlyList<SessionData> sessions,
        bool keepTrades = false)
    {
        var result = new OptimizationResult { Config = config };
        if (keepTrades) result.DetailedTrades = new List<SimulatedTrade>();
        decimal balance = 0m, maxBalance = 0m;
        var logic = new OrbStrategy { EsTrailingDistance = config.EsTrailDollars };

        foreach (var s in sessions)
        {
            var firstEntry = FindFirstEntry(s, config);
            if (firstEntry is null)
            {
                result.SkippedDays++;
                continue;
            }

            var (signalIndex, side) = firstEntry.Value;
            var first = SimulateCached(s, logic, signalIndex, side, config.EsQuantity,
                isMes: false, config.Risk.MaxRisk, config.Risk.FallbackRisk,
                config.EsTrailDollars);
            if (first is null)
            {
                result.SkippedDays++;
                continue;
            }
            Accumulate(first);

            if (config.MesQuantity > 0 && first.Stopped && first.Net < 0m)
            {
                var reversal = FindStage6Entry(s, first);
                if (reversal is { } entry)
                {
                    // ES OHLC is only a price proxy for MES; true MES bars are
                    // unavailable in the user's saved ES-only dataset.
                    var mes = SimulateCached(s, logic, entry.SignalIndex, entry.Side,
                        config.MesQuantity, isMes: true,
                        OrbStrategy.Stage6MaxInitialRisk,
                        OrbStrategy.Stage6DefaultInitialStopRisk, config.EsTrailDollars);
                    if (mes is not null) Accumulate(mes);
                }
            }
        }

        return result;

        void Accumulate(SimulatedTrade t)
        {
            if (t.IsMes) result.MesTrades++;
            else result.EsTrades++;
            result.Gross += t.Gross;
            result.Costs += t.EstimatedCosts;
            result.Net += t.Net;
            if (t.Net > 0) result.WinningTrades++;
            balance += t.Net;
            maxBalance = Math.Max(maxBalance, balance);
            result.MaxDrawdown = Math.Max(result.MaxDrawdown, maxBalance - balance);
            result.DetailedTrades?.Add(t);
        }
    }

    private static (int SignalIndex, TradeDirection Side)? FindFirstEntry(
        SessionData day, OptimizationConfig config)
    {
        if (day.Range is not { } orb ||
            orb.Width < config.Range.Min || orb.Width > config.Range.Max)
            return null;
        var bars = day.Bars;
        int pendingIndex = -1;
        TradeDirection pendingSide = default;
        bool hasRetested = false;

        for (int i = 0; i + 1 < bars.Length; i++)
        {
            var time = MarketTime.ToEastern(bars[i].T).TimeOfDay;
            if (time < OrbEnd) continue;
            // Cutoff is for the signal candle CLOSE, not the bar's start.
            if (time.Add(TimeSpan.FromMinutes(1)) > config.Cutoff) break;

            if (pendingIndex >= 0)
            {
                if (i - pendingIndex > 5) pendingIndex = -1;
                else if (i > pendingIndex)
                {
                    var c = bars[i].C;
                    if (pendingSide == TradeDirection.Long ? c.L <= orb.High : c.H >= orb.Low)
                        hasRetested = true;
                    if (hasRetested &&
                        Breakout(c.C, orb, config.BreakoutPoints) == pendingSide &&
                        VwapPass(day, i, pendingSide, config.Vwap) &&
                        bars[i + 1].T == bars[i].T.AddMinutes(1))
                        return (i, pendingSide);
                }
            }

            // On a new, valid breakout, either enter immediately or begin waiting
            // for a five-minute OR-boundary retest and subsequent renewed close.
            var direction = Breakout(bars[i].C.C, orb, config.BreakoutPoints);
            if (direction is not { } side ||
                !VwapPass(day, i, side, config.Vwap) ||
                !VolumePass(day, i, config.VolumeMultiplier))
                continue;
            if (!config.Retest)
                return bars[i + 1].T == bars[i].T.AddMinutes(1)
                    ? (i, side) : null;

            if (pendingIndex < 0)
            {
                pendingIndex = i;
                pendingSide = side;
                hasRetested = false;
            }
        }
        return null;
    }

    private static TradeDirection? Breakout(decimal price, OpeningRange orb, decimal buffer) =>
        price > orb.High + buffer ? TradeDirection.Long :
        price < orb.Low - buffer ? TradeDirection.Short : null;

    private static bool VwapPass(SessionData day, int i, TradeDirection side, VwapRule mode)
    {
        if (mode == VwapRule.Off) return true;
        if (day.Vwaps[i] is not decimal now) return false;
        if (side == TradeDirection.Long
            ? day.Bars[i].C.C <= now : day.Bars[i].C.C >= now)
            return false;
        if (mode == VwapRule.Direction) return true;
        if (i < 5 || day.Vwaps[i - 5] is not decimal earlier) return false;
        return side == TradeDirection.Long ? now > earlier : now < earlier;
    }

    private static bool VolumePass(SessionData day, int i, decimal multiple)
    {
        if (multiple <= 0m) return true;
        return day.VolumeAverages[i] is decimal avg && avg > 0m &&
               day.Bars[i].C.V >= avg * multiple;
    }

    private static (int SignalIndex, TradeDirection Side)? FindStage6Entry(
        SessionData day, SimulatedTrade stopped)
    {
        var orb = day.Range;
        if (orb is null || stopped.ExitIndex < 0) return null;
        var inRange = stopped.Exit >= orb.Low && stopped.Exit <= orb.High;
        var bars = day.Bars;
        for (var i = stopped.ExitIndex + 1; i + 1 < bars.Length; i++)
        {
            var time = MarketTime.ToEastern(bars[i].T).TimeOfDay;
            if (time.Add(TimeSpan.FromMinutes(1)) >= Stage6Cutoff) break;
            var bar = bars[i].C;
            if (!inRange && bar.L <= orb.High && bar.H >= orb.Low)
                inRange = true;
            if (!inRange) continue;
            var direction = Breakout(bar.C, orb, 0m);
            if (direction is { } side &&
                bars[i + 1].T == bars[i].T.AddMinutes(1))
                return (i, side);
        }
        return null;
    }

    private static SimulatedTrade? SimulateCached(
        SessionData day, OrbStrategy logic, int signalIndex, TradeDirection side,
        int qty, bool isMes, decimal riskCap, decimal fallbackRisk,
        decimal esTrailingDistance)
    {
        var key = new TradeKey(day.Day, signalIndex, side, qty, isMes,
            riskCap, fallbackRisk, isMes ? 0m : esTrailingDistance);
        if (TradeCache.TryGetValue(key, out var cached)) return cached;
        var calculated = SimulateTrade(day, logic, signalIndex, side, qty,
            isMes, riskCap, fallbackRisk);
        TradeCache[key] = calculated;
        return calculated;
    }

    private static SimulatedTrade? SimulateTrade(
        SessionData day, OrbStrategy logic, int signalIndex, TradeDirection side,
        int qty, bool isMes, decimal riskCap, decimal fallbackRisk)
    {
        var bars = day.Bars;
        if (qty <= 0 || signalIndex + 1 >= bars.Length ||
            bars[signalIndex + 1].T != bars[signalIndex].T.AddMinutes(1) ||
            day.Range is not { } orb)
            return null;

        var entryBar = bars[signalIndex + 1];
        var entry = entryBar.C.O;
        var value = isMes ? OrbStrategy.MesPointValue : OrbStrategy.EsPointValue;
        var initial = logic.CalculateInitialStop(side, entry, orb,
            allowance: 10m * qty, pointValue: value, quantity: qty,
            maxInitialRisk: riskCap, defaultInitialStopRisk: fallbackRisk);
        if (side == TradeDirection.Long ? initial.Stop >= entry : initial.Stop <= entry)
            return null;

        var levels = side == TradeDirection.Long ? day.LongReferences : day.ShortReferences;
        var trade = logic.CreateTrade(side, entryBar.Contract, qty, entry,
            initial.Stop, levels.Intraday, levels.PreviousDay,
            null, value, isMes);
        trade.ConfirmedStop = initial.Stop;

        // Keep the same seven-day FVG lookback as Orb's history request.
        // The original code can be costly; the optimizer exposes progress.
        var known = new List<Candle>(day.PriorHistory.Length + bars.Length);
        known.AddRange(day.PriorHistory);
        for (var j = 0; j <= signalIndex; j++)
            if (MarketTime.ToEastern(bars[j].T).TimeOfDay >= OrbEnd)
                known.Add(bars[j].C);

        decimal mfePoints = 0m;
        for (var i = signalIndex + 1; i < bars.Length; i++)
        {
            var candle = bars[i].C;
            if (i > signalIndex + 1 &&
                bars[i].T != bars[i - 1].T.AddMinutes(1))
                return null; // Never silently backtest across a missing minute.

            var stop = trade.ConfirmedStop ?? initial.Stop;
            var touched = side == TradeDirection.Long
                ? candle.L <= stop : candle.H >= stop;
            if (touched)
            {
                var favorableOpen = side == TradeDirection.Long
                    ? candle.O - entry : entry - candle.O;
                mfePoints = Math.Max(mfePoints, Math.Max(0m, favorableOpen));
                // A gap through an already-active stop receives the worse open.
                var exit = side == TradeDirection.Long
                    ? Math.Min(candle.O, stop) : Math.Max(candle.O, stop);
                return Make("stop", exit);
            }

            var move = side == TradeDirection.Long
                ? candle.H - entry : entry - candle.L;
            mfePoints = Math.Max(mfePoints, Math.Max(0m, move));
            known.Add(candle);

            // Actual Orb evaluates on quote ticks; historical 1-minute bars permit
            // only candle-close evaluation. New protective stops apply next bar.
            for (var step = 0; step < 3; step++)
            {
                var action = logic.GetNextStageAction(trade, candle.C, orb, known);
                if (action is null) break;
                if (action.DesiredStop is decimal candidate)
                {
                    candidate = side == TradeDirection.Long
                        ? Math.Floor(candidate / OrbStrategy.TickSize) * OrbStrategy.TickSize
                        : Math.Ceiling(candidate / OrbStrategy.TickSize) * OrbStrategy.TickSize;
                    if (trade.ConfirmedStop is not decimal existing ||
                        logic.IsMoreProtective(side, candidate, existing))
                    {
                        if (!logic.IsValidProtectiveStop(trade, candidate, candle.C))
                            return Make("invalid-stop-close", candle.C);
                        trade.ConfirmedStop = candidate;
                    }
                }
                if (action.Stage is 3 or 4) logic.CompleteStage(trade, action);
                else break;
            }

            if (MarketTime.ToEastern(bars[i].T).TimeOfDay ==
                new TimeSpan(15, 59, 0))
                return Make("4pm-close", candle.C);

            SimulatedTrade Make(string reason, decimal price) =>
                new(day.Day, entryBar.Contract, isMes, qty, side,
                    entryBar.T, entry, bars[i].T.AddMinutes(1), price,
                    reason, mfePoints, i);
        }
        return null; // Data ended without a stop or complete 4 PM close.
    }
}
