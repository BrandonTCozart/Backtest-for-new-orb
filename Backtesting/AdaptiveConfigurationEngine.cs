using Orb;

namespace Backtest_for_new_orb;

// Coarse, point-in-time buckets intentionally limit the number of fitted
// experts. Missing references / shock / thin liquidity are NO-TRADE.
public enum AdaptiveMarketState { Trend, Range, Transition, Unavailable }
public enum CandleFeatureRule { None, RejectionOrEngulfing }
public enum AdaptiveConfigurationMode { Baseline, Adaptive, AdaptiveCandles }

public sealed record AdaptiveConfigurationRule(
    MultiStrategyVariant Variant, CandleFeatureRule CandleRule);

public sealed record AdaptiveConfigurationModel(
    IReadOnlyDictionary<MultiStrategyKind,
        IReadOnlyDictionary<AdaptiveMarketState, AdaptiveConfigurationRule>> Rules)
{
    public int ExpertCount => Rules.Sum(x => x.Value.Count);
}

public static class AdaptiveConfigurationEngine
{
    // Predefined regime cutoffs: fitted neither to the later test block
    // nor to the previously inspected 2025-2026 forward results.
    private static readonly RegimeThresholds Thresholds = new();

    public static AdaptiveMarketState State(MultiSession session, MultiStrategySlot slot)
    {
        var observed = MarketRegimeEngine.ForSlot(session, slot);
        return MarketRegimeEngine.Classify(observed, Thresholds) switch
        {
            MarketRegime.Trend => AdaptiveMarketState.Trend,
            MarketRegime.Range => AdaptiveMarketState.Range,
            MarketRegime.Uncertain => AdaptiveMarketState.Transition,
            _ => AdaptiveMarketState.Unavailable
        };
    }

    // Candle is always the COMPLETED bar immediately preceding next-open
    // entry; its geometry is never inspected before that time.
    public static bool CandlePass(MultiSession session, MultiStrategyTrade trade,
        CandleFeatureRule rule)
    {
        if (rule == CandleFeatureRule.None) return true;
        var bars = trade.EntryAt >= session.Rth[0].T &&
                   trade.EntryAt <= session.Rth[^1].T
            ? session.Rth : session.Overnight;
        if (bars is null) return false;
        var i = Array.FindIndex(bars, b => b.T == trade.EntryAt);
        if (i < 2 || bars[i - 1].T.AddMinutes(1) != trade.EntryAt)
            return false;
        var c = bars[i - 1].C;
        var p = bars[i - 2].C;
        var span = c.H - c.L;
        if (span <= 0m) return false;
        var body = Math.Abs(c.C - c.O);
        var lower = Math.Min(c.C, c.O) - c.L;
        var upper = c.H - Math.Max(c.C, c.O);
        var longSide = trade.Side == TradeDirection.Long;
        var wick = longSide
            ? lower >= Math.Max(0.25m, 1.5m * body) &&
              (c.H - c.C) <= span / 3m
            : upper >= Math.Max(0.25m, 1.5m * body) &&
              (c.C - c.L) <= span / 3m;
        var engulf = longSide
            ? p.C < p.O && c.C > c.O && c.O <= p.C && c.C >= p.O
            : p.C > p.O && c.C < c.O && c.O >= p.C && c.C <= p.O;
        return wick || engulf;
    }

    // Two strongest diverse candidates per (direction, exit type). The pool
    // is assembled using only each fold's TRAINING profitability, not OOS.
    internal static void Consider(
        Dictionary<(MultiStrategyKind, SignalMode, MultiExitMode),
            List<MultiStrategyRunner.Choice>> pool,
        MultiStrategyRunner.Choice candidate)
    {
        if (candidate.Score.Net <= 0m || candidate.Robustness <= 0m)
            return;
        var v = candidate.Variant;
        var key = (v.Kind, v.SignalMode, v.ExitMode);
        if (!pool.TryGetValue(key, out var choices))
            pool[key] = choices = new List<MultiStrategyRunner.Choice>(2);
        static bool SameEntry(MultiStrategyVariant a, MultiStrategyVariant b) =>
            a.Lookback == b.Lookback && a.Threshold == b.Threshold &&
            a.StartShiftMinutes == b.StartShiftMinutes &&
            a.OrbCutoffMinutes == b.OrbCutoffMinutes;
        var existing = choices.FindIndex(c => SameEntry(c.Variant, v));
        if (existing >= 0)
        {
            if (!MultiStrategyRunner.BetterAdaptiveCandidate(candidate, choices[existing]))
                return;
            choices.RemoveAt(existing);
        }
        else if (choices.Count == 2 &&
                 !MultiStrategyRunner.BetterAdaptiveCandidate(candidate, choices[^1]))
            return;
        choices.Add(candidate);
        choices.Sort((a, b) =>
            MultiStrategyRunner.BetterAdaptiveCandidate(a, b) ? -1 :
            MultiStrategyRunner.BetterAdaptiveCandidate(b, a) ? 1 : 0);
        if (choices.Count > 2) choices.RemoveAt(2);
    }

    internal static AdaptiveConfigurationModel Select(
        IReadOnlyDictionary<MultiStrategyKind, MultiStrategyRunner.Choice> baseline,
        Dictionary<(MultiStrategyKind, SignalMode, MultiExitMode),
            List<MultiStrategyRunner.Choice>> pool,
        IReadOnlyList<MultiSession> sessions, int from, int to,
        bool allowCandleFilter)
    {
        var result = new Dictionary<MultiStrategyKind,
            IReadOnlyDictionary<AdaptiveMarketState, AdaptiveConfigurationRule>>();
        var dayArray = sessions.Skip(from).Take(to - from).Select(s => s.Day).ToArray();
        var fakeFold = new[] { new WalkForwardFold(1, 0, dayArray.Length,
            dayArray.Length, dayArray.Length) };
        foreach (var (kind, initial) in baseline)
        {
            var candidates = pool.Where(x => x.Key.Item1 == kind)
                .SelectMany(x => x.Value)
                .Append(initial)
                .Where(c => c.Variant.SignalMode != SignalMode.Disabled)
                .DistinctBy(c => (c.Variant.Id, c.Variant.SignalMode)).ToArray();
            var chosen = new Dictionary<AdaptiveMarketState, AdaptiveConfigurationRule>();
            foreach (var state in new[] { AdaptiveMarketState.Trend,
                         AdaptiveMarketState.Range, AdaptiveMarketState.Transition })
            {
                decimal bestScore = 0m;
                AdaptiveConfigurationRule? winner = null;
                foreach (var candidate in candidates)
                foreach (var candleRule in allowCandleFilter
                    ? new[] { CandleFeatureRule.None, CandleFeatureRule.RejectionOrEngulfing }
                    : new[] { CandleFeatureRule.None })
                {
                    var valid = new Dictionary<DateOnly, MultiStrategyTrade>();
                    for (var j = from; j < to; j++)
                    {
                        var s = sessions[j];
                        if (State(s, candidate.Variant.Slot) != state ||
                            !candidate.Trades.TryGetValue(s.Day, out var trade) ||
                            !IsAfterObservation(s, trade) ||
                            !CandlePass(s, trade, candleRule))
                            continue;
                        valid[s.Day] = trade;
                    }
                    var scored = MultiStrategyRunner.ScoreCandidateRangesDetailed(
                        valid, dayArray, fakeFold);
                    var x = scored.Folds[0];
                    // Both sides of training must support this specialist.
                    // A stricter $20 ES round trip must remain profitable.
                    var minimum = Math.Max(12, (to - from + 39) / 40);
                    if (x.Train.Trades < minimum ||
                        x.Early.Trades < 4 || x.Late.Trades < 4 ||
                        x.Train.Net <= 10m * x.Train.Trades ||
                        x.Curve.ProfitableMonths < Math.Max(2, x.Curve.TotalMonths / 6))
                        continue;
                    var robust = MultiStrategyRunner.ConsistencyRobustness(
                        x.Train, x.Early, x.Late, x.Curve);
                    if (robust <= 0m) continue;
                    if (robust > bestScore ||
                        robust == bestScore && winner is not null &&
                        (candidate.Variant.Id < winner.Variant.Id ||
                         candidate.Variant.Id == winner.Variant.Id &&
                         candleRule < winner.CandleRule))
                    {
                        bestScore = robust;
                        winner = new AdaptiveConfigurationRule(candidate.Variant, candleRule);
                    }
                }
                if (winner is not null) chosen[state] = winner;
            }
            result[kind] = chosen;
        }
        return new AdaptiveConfigurationModel(result);
    }

    private static bool IsAfterObservation(
        MultiSession session, MultiStrategyTrade trade)
    {
        var observation = MarketRegimeEngine.ForSlot(session,
            trade.Kind switch
            {
                MultiStrategyKind.Orb => MultiStrategySlot.Morning,
                _ => trade.EntryAt < session.Rth[0].T
                    ? MultiStrategySlot.Overnight
                    : trade.EntryAt >= session.Rth[210].T
                        ? MultiStrategySlot.Afternoon
                        : MultiStrategySlot.Morning
            });
        return observation is not null && trade.EntryAt >= observation.AvailableAt;
    }

    // Reconstruct actual executable trades per frozen expert. Each expert's
    // full-period simulation is independent; only its matching market-state
    // days can contribute. The target period NEVER influences rule selection.
    internal static Dictionary<MultiStrategyKind, MultiStrategyRunner.Choice> Apply(
        AdaptiveConfigurationModel model,
        IReadOnlyDictionary<MultiStrategyKind, MultiStrategyRunner.Choice> baseline,
        IReadOnlyList<MultiSession> sessions, IReadOnlyList<SessionData> orb,
        int trainingFrom, int trainingTo)
    {
        var index = sessions.ToDictionary(s => s.Day);
        var training = trainingFrom >= 0 && trainingTo > trainingFrom;
        var sessionDays = training
            ? sessions.Skip(trainingFrom).Take(trainingTo - trainingFrom)
                .Select(x => x.Day).ToArray()
            : Array.Empty<DateOnly>();
        var fold = training
            ? new[] { new WalkForwardFold(1, 0, sessionDays.Length,
                sessionDays.Length, sessionDays.Length) }
            : Array.Empty<WalkForwardFold>();
        var result = new Dictionary<MultiStrategyKind, MultiStrategyRunner.Choice>();
        foreach (var (kind, original) in baseline)
        {
            var merged = new Dictionary<DateOnly, MultiStrategyTrade>();
            var routes = model.Rules.GetValueOrDefault(kind);
            if (routes is not null)
                foreach (var (state, rule) in routes)
                {
                    var generated = MultiStrategyEngine.Generate(rule.Variant, sessions, orb);
                    foreach (var (day, trade) in generated)
                        if (index.TryGetValue(day, out var s) &&
                            State(s, rule.Variant.Slot) == state &&
                            IsAfterObservation(s, trade) &&
                            CandlePass(s, trade, rule.CandleRule))
                        {
                            if (!merged.TryAdd(day, trade))
                                throw new InvalidOperationException(
                                    "Multiple adaptive variants fired on the same session.");
                        }
                }
            if (!training)
            {
                result[kind] = original with { Trades = merged, IsAdaptive = true };
                continue;
            }
            var detailed = MultiStrategyRunner.ScoreCandidateRangesDetailed(
                merged, sessionDays, fold);
            var score = detailed.Folds[0];
            var robust = MultiStrategyRunner.ConsistencyRobustness(
                score.Train, score.Early, score.Late, score.Curve);
            result[kind] = original with
            {
                Trades = merged,
                Score = score.Train,
                Early = score.Early,
                Late = score.Late,
                Robustness = robust,
                Consistency = score.Curve,
                IsAdaptive = true
            };
        }
        return result;
    }
}
