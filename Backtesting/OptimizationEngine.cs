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
    private readonly record struct EntryKey(
        DateOnly Day, TimeSpan Cutoff, decimal RangeMin, decimal RangeMax,
        VwapRule Vwap, decimal BreakoutPoints, decimal VolumeMultiplier, bool Retest);
    // Mutable ORB entry/trade caches must belong to individual CPU workers,
    // otherwise concurrent ORB candidates corrupt shared Dictionary state.
    // The global epoch invalidates ALL thread-local caches when SQL dataset
    // changes, including thread-pool workers not currently scheduled.
    private sealed class WorkerCache
    {
        internal long Epoch;
        internal int Candidates;
        internal readonly Dictionary<TradeKey, SimulatedTrade?> Trades = new();
        internal readonly Dictionary<EntryKey,
            (int SignalIndex, TradeDirection Side)?> Entries = new();

        internal void Reset()
        {
            Candidates = 0;
            Entries.Clear();
            Trades.Clear();
        }
    }

    [ThreadStatic] private static WorkerCache? _threadCache;
    private static long _cacheEpoch = 1;
    private static WorkerCache GetCache()
    {
        var cache = _threadCache ??= new WorkerCache();
        var epoch = System.Threading.Volatile.Read(ref _cacheEpoch);
        if (cache.Epoch != epoch)
        {
            cache.Reset();
            cache.Epoch = epoch;
        }
        return cache;
    }

    // Both caches are scoped to the currently loaded candle dataset.
    public static void ClearCaches()
    {
        System.Threading.Interlocked.Increment(ref _cacheEpoch);
        _ = GetCache();
    }

    // Prevent per-worker ORB trade caches from growing indefinitely over
    // years of candles; mirrors the original 1,200-candidate purge cadence.
    internal static void TrimCurrentWorkerCaches()
    {
        var cache = GetCache();
        if (++cache.Candidates >= 1200) cache.Reset();
    }

    private sealed record DayOutcome(DateOnly Day, SimulatedTrade? First, SimulatedTrade? Reversal);

    public static OptimizationResult Evaluate(
        OptimizationConfig config, IReadOnlyList<SessionData> sessions,
        bool keepTrades = false)
    {
        Validate(config);
        return Aggregate(config, ComputeDayOutcomes(config, sessions), keepTrades);
    }

    // Simulate the expensive intraday entry/stop logic only ONCE per base
    // configuration. Then test every weekday subset by cheaply filtering
    // the already-computed daily trades in their original date order.
    // This makes 31 weekday subsets practical without rerunning 31x trades.
    public static IEnumerable<OptimizationResult> EvaluateWeekdayVariants(
        OptimizationConfig baseConfig, IReadOnlyList<SessionData> sessions)
    {
        Validate(baseConfig);
        var outcomes = ComputeDayOutcomes(baseConfig, sessions);
        for (var mask = 1; mask <= WeekdaySelection.All; mask++)
        {
            var candidate = baseConfig with {
                WeekdayMask = mask,
                Id = checked((baseConfig.Id - 1) * WeekdaySelection.All + mask)
            };
            yield return Aggregate(candidate, outcomes, keepTrades: false);
        }
    }

    // Daily trade paths are simulated once per base configuration.
    // Every weekday/event combination receives three fold-training scores
    // and a separate full-dataset score (FoldIndex == folds.Count).
    // The full-dataset score never influences the fold winners.
    public static IEnumerable<(int FoldIndex, OptimizationResult Score)>
        EvaluateWalkForwardTrainingVariants(
            OptimizationConfig baseConfig,
            IReadOnlyList<SessionData> sessions,
            IReadOnlyList<WalkForwardFold> folds)
    {
        Validate(baseConfig);
        var outcomes = ComputeDayOutcomes(baseConfig, sessions);
        foreach (var rule in Enum.GetValues<EconomicEventRule>())
        {
            for (var mask = 1; mask <= WeekdaySelection.All; mask++)
            {
                var candidate = baseConfig with
                {
                    WeekdayMask = mask,
                    EventRule = rule,
                    Id = checked(((baseConfig.Id - 1) * WeekdaySelection.All +
                        (mask - 1)) * 4 + (int)rule + 1)
                };

                // The training folds are chronological prefixes (0..end).
                // Score all 3 fold prefixes and the full sample in ONE pass
                // instead of rescanning the same days four times.
                var scores = AggregateTrainingPrefixes(candidate, outcomes, folds);
                for (var foldIndex = 0; foldIndex < scores.Length; foldIndex++)
                    yield return (foldIndex, scores[foldIndex]);
            }
        }
    }

    private static OptimizationResult[] AggregateTrainingPrefixes(
        OptimizationConfig config,
        IReadOnlyList<DayOutcome> days,
        IReadOnlyList<WalkForwardFold> folds)
    {
        var snapshots = new OptimizationResult[folds.Count + 1];
        var running = new OptimizationResult { Config = config };
        decimal balance = 0m, peak = 0m;
        var nextFold = 0;

        for (var index = 0; index < days.Count; index++)
        {
            AccumulateDay(config, days[index], running, ref balance, ref peak);

            // Each snapshot must be independent: the accumulator continues
            // through later dates, but fold winners must not change.
            while (nextFold < folds.Count &&
                   index + 1 == folds[nextFold].TrainingEnd)
            {
                if (folds[nextFold].TrainingStart != 0)
                    throw new InvalidOperationException(
                        "Prefix scoring requires expanding-window folds beginning at index zero.");
                snapshots[nextFold] = CopyScore(running);
                nextFold++;
            }
        }

        if (nextFold != folds.Count)
            throw new InvalidOperationException(
                "Training folds must have strictly increasing endpoints within the historical sessions.");
        snapshots[folds.Count] = running;
        return snapshots;
    }

    private static OptimizationResult CopyScore(OptimizationResult value) => new()
    {
        Config = value.Config,
        Net = value.Net,
        Gross = value.Gross,
        Costs = value.Costs,
        MaxDrawdown = value.MaxDrawdown,
        EsTrades = value.EsTrades,
        MesTrades = value.MesTrades,
        WinningTrades = value.WinningTrades,
        SkippedDays = value.SkippedDays,
        DetailedTrades = value.DetailedTrades is null ? null : [.. value.DetailedTrades]
    };

    private static void Validate(OptimizationConfig config)
    {
        if (config.EsQuantity is < 1 or > 2 || config.MesQuantity is < 0 or > 20)
            throw new ArgumentOutOfRangeException(nameof(config),
                "Optimizer position caps: 1–2 ES; 0–20 MES.");
        if (config.WeekdayMask is < 1 or > WeekdaySelection.All)
            throw new ArgumentOutOfRangeException(nameof(config),
                "Weekdays must be a non-empty combination of Monday–Friday.");
    }

    private static List<DayOutcome> ComputeDayOutcomes(
        OptimizationConfig config, IReadOnlyList<SessionData> sessions)
    {
        var outcomes = new List<DayOutcome>(sessions.Count);
        var logic = new OrbStrategy { EsTrailingDistance = config.EsTrailDollars };

        foreach (var session in sessions)
        {
            var signal = FindFirstEntryCached(session, config);
            if (signal is not { } entry)
            {
                outcomes.Add(new DayOutcome(session.Day, null, null));
                continue;
            }

            var first = SimulateCached(session, logic, entry.SignalIndex,
                entry.Side, config.EsQuantity, isMes: false,
                config.Risk.MaxRisk, config.Risk.FallbackRisk,
                config.EsTrailDollars);

            SimulatedTrade? reversal = null;
            if (first is not null && config.MesQuantity > 0 &&
                first.Stopped && first.Net < 0m)
            {
                var second = FindStage6Entry(session, first);
                if (second is { } resignal)
                {
                    // MES trades are ES-candle PRICE PROXIES, not real MES bars.
                    reversal = SimulateCached(session, logic, resignal.SignalIndex,
                        resignal.Side, config.MesQuantity, isMes: true,
                        OrbStrategy.Stage6MaxInitialRisk,
                        OrbStrategy.Stage6DefaultInitialStopRisk,
                        config.EsTrailDollars);
                }
            }
            outcomes.Add(new DayOutcome(session.Day, first, reversal));
        }
        return outcomes;
    }

    private static OptimizationResult Aggregate(
        OptimizationConfig config, IReadOnlyList<DayOutcome> days, bool keepTrades) =>
        AggregateRange(config, days, 0, days.Count, keepTrades);

    private static OptimizationResult AggregateRange(
        OptimizationConfig config, IReadOnlyList<DayOutcome> days,
        int start, int end, bool keepTrades)
    {
        var result = new OptimizationResult { Config = config };
        if (keepTrades) result.DetailedTrades = new List<SimulatedTrade>();
        decimal balance = 0m, peak = 0m;

        for (var dayIndex = start; dayIndex < end; dayIndex++)
            AccumulateDay(config, days[dayIndex], result, ref balance, ref peak);

        return result;
    }

    // Used by both prefix training and ordinary replay, keeping the weekday,
    // economic-calendar, cost, trade-count and drawdown rules identical.
    private static void AccumulateDay(
        OptimizationConfig config, DayOutcome day, OptimizationResult result,
        ref decimal balance, ref decimal peak)
    {
        if (!WeekdaySelection.Includes(config.WeekdayMask, day.Day.DayOfWeek))
            return; // Excluded weekdays do NOT count as skipped sessions.

        if (day.First is null)
        {
            result.SkippedDays++;
            return;
        }

        if (!EconomicCalendar.AllowsEntry(config.EventRule, day.Day, day.First.EntryAt))
            return; // The calendar filter excludes this entry on this date.

        AccumulateTrade(day.First, result, ref balance, ref peak);
        if (day.Reversal is not null &&
            EconomicCalendar.AllowsEntry(config.EventRule, day.Day, day.Reversal.EntryAt))
            AccumulateTrade(day.Reversal, result, ref balance, ref peak);
    }

    private static void AccumulateTrade(
        SimulatedTrade trade, OptimizationResult result,
        ref decimal balance, ref decimal peak)
    {
        if (trade.IsMes) result.MesTrades++;
        else result.EsTrades++;
        result.Gross += trade.Gross;
        result.Costs += trade.EstimatedCosts;
        result.Net += trade.Net;
        if (trade.Net > 0) result.WinningTrades++;
        balance += trade.Net;
        peak = Math.Max(peak, balance);
        result.MaxDrawdown = Math.Max(result.MaxDrawdown, peak - balance);
        result.DetailedTrades?.Add(trade);
    }

    // Entry detection depends only on signal filters, NOT position size,
    // initial risk, stop trailing or Stage-6 MES quantity. The grid repeats
    // each signal-filter combination across those 48 risk/position variants.
    // Reuse the first signal instead of rescanning minute candles each time.
    private static (int SignalIndex, TradeDirection Side)? FindFirstEntryCached(
        SessionData day, OptimizationConfig config)
    {
        var key = new EntryKey(day.Day, config.Cutoff, config.Range.Min,
            config.Range.Max, config.Vwap, config.BreakoutPoints,
            config.VolumeMultiplier, config.Retest);
        var cache = GetCache().Entries;
        if (cache.TryGetValue(key, out var entry))
            return entry;
        entry = FindFirstEntry(day, config);
        cache.Add(key, entry);
        return entry;
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
        var cache = GetCache().Trades;
        if (cache.TryGetValue(key, out var cached)) return cached;
        var calculated = SimulateTrade(day, logic, signalIndex, side, qty,
            isMes, riskCap, fallbackRisk);
        cache[key] = calculated;
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
