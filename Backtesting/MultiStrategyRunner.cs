using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

public sealed record MultiScore(
    int Trades, int Wins, decimal Net, decimal Gross, decimal Costs,
    decimal Drawdown, decimal? ProfitFactor)
{
    public decimal WinRate => Trades == 0 ? 0m : 100m * Wins / Trades;
    public decimal Average => Trades == 0 ? 0m : Net / Trades;

    public static MultiScore From(IEnumerable<MultiStrategyTrade> source)
    {
        var trades = source.OrderBy(t => t.ExitAt).ThenBy(t => t.EntryAt).ToArray();
        decimal net = 0m, gross = 0m, costs = 0m, peak = 0m, drawdown = 0m;
        decimal gains = 0m, losses = 0m;
        var wins = 0;
        foreach (var t in trades)
        {
            net += t.Net;
            gross += t.Gross;
            costs += t.Costs;
            if (t.IsWin) { wins++; gains += t.Net; }
            else losses -= t.Net;
            peak = Math.Max(peak, net);
            drawdown = Math.Max(drawdown, peak - net);
        }
        return new MultiScore(trades.Length, wins, net, gross, costs,
            drawdown, losses > 0m ? gains / losses : null);
    }
}

public enum MultiPortfolioMode { Single, FixedSessions, AdaptiveRegimes }

public sealed record MultiPortfolioPolicy(
    MultiPortfolioMode Mode, MultiStrategyKind? SingleStrategy = null,
    decimal NightThreshold = 1m, decimal OrbThreshold = 2m,
    decimal MorningThreshold = 1m, decimal AfternoonThreshold = 2m)
{
    public string Description => Mode switch
    {
        MultiPortfolioMode.Single => "Single_" + SingleStrategy,
        MultiPortfolioMode.FixedSessions => "Fixed_best_per_session",
        _ => "Adaptive_night" + NightThreshold + "_orb" + OrbThreshold +
             "_morning" + MorningThreshold + "_afternoon" + AfternoonThreshold
    };
}

public static class MultiStrategyRunner
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const decimal DailyLossStop = 1000m;
    private const int MaxDailyTrades = 3;

    private sealed record Choice(
        MultiStrategyVariant Variant, MultiScore Score,
        IReadOnlyDictionary<DateOnly, MultiStrategyTrade> Trades,
        decimal Robustness, MultiScore Early, MultiScore Late);
    private sealed record PortfolioOutcome(
        MultiPortfolioPolicy Policy, MultiScore Score, List<MultiStrategyTrade> Trades);

    public static int Run(
        List<PriceBar> active, List<PriceBar> all, string root,
        MultiSearchProfile profile = MultiSearchProfile.Deep)
    {
        // No report artifacts are written. The SQL run reports results in the terminal.
        Console.WriteLine();
        Console.WriteLine("=== DEEP MULTI-STRATEGY ES RESEARCH (NO LIVE ORDERS) ===");
        var sessions = MultiStrategyData.Build(active, all, Path.Combine(root, "data"));
        if (sessions.Count < 20)
        {
            Console.Error.WriteLine("Need at least 20 validated full-length RTH sessions.");
            return 2;
        }
        if (sessions[0].Day < new DateOnly(2025, 10, 6) ||
            sessions[^1].Day > new DateOnly(2026, 10, 30))
            Console.WriteLine("WARNING: scheduled economic-event calendar does not cover every tested date.");

        var days = sessions.Select(x => x.Day).ToHashSet();
        var originalOrb = OptimizationData.Prepare(active, all)
            .Where(x => x.Range is not null && days.Contains(x.Day))
            .OrderBy(x => x.Day).ToArray();
        var folds = CreateResearchFolds(sessions.Count);
        var variants = MultiStrategyEngine.EnumerateVariants(profile);
        var kinds = Enum.GetValues<MultiStrategyKind>();
        var countByKind = variants.GroupBy(v => v.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine("Search profile " + profile + ": " +
                          variants.Count.ToString("N0", Ci) +
                          " configurations across " + kinds.Length + " strategy types.");
        foreach (var kind in kinds)
            Console.WriteLine("  " + kind + ": " + countByKind[kind].ToString("N0", Ci) +
                              " configurations");
        Console.WriteLine(folds.Length + " chronological walk-forward folds; robustness-scored training split.");
        Console.WriteLine("Session positions do not overlap; 1 ES on new strategies; " +
                          "stops capped at $600 including estimated $10/contract costs.");
        Console.WriteLine("Console-only reporting; only current candidate and selected winners are retained in memory.");

        var bestForFold = folds.Select(_ => new Dictionary<MultiStrategyKind, Choice>())
            .ToArray();
        var fullProfit = new Dictionary<MultiStrategyKind, Choice>();
        var fullWinRate = new Dictionary<MultiStrategyKind, Choice>();
        var eligible = new int[folds.Length];
        var minimumFull = Math.Max(10, (sessions.Count + 3) / 4);
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();
        {
            // Full parameter grid still evaluated, without per-candidate disk I/O.
            var tested = 0;
            foreach (var variant in variants)
            {
                // Stream ONE candidate at a time; outcomes for losing candidates
                // are discarded rather than retaining millions of trades.
                var trades = MultiStrategyEngine.Generate(variant, sessions, originalOrb);
                var full = ScoreRange(trades, sessions, 0, sessions.Count);
                var fullQualifies = full.Trades >= minimumFull;
                if (fullQualifies)
                {
                    var candidate = new Choice(variant, full, trades, full.Net,
                        full, full);
                    if (!fullProfit.TryGetValue(variant.Kind, out var previous) ||
                        Better(full, variant.Id, previous.Score, previous.Variant.Id))
                        fullProfit[variant.Kind] = candidate;
                    if (!fullWinRate.TryGetValue(variant.Kind, out previous) ||
                        HigherWinRate(full, variant.Id, previous.Score, previous.Variant.Id))
                        fullWinRate[variant.Kind] = candidate;
                }

                for (var f = 0; f < folds.Length; f++)
                {
                    var fold = folds[f];
                    var mid = fold.TrainingStart + fold.TrainingSessions / 2;
                    var early = ScoreRange(trades, sessions, fold.TrainingStart, mid);
                    var late = ScoreRange(trades, sessions, mid, fold.TrainingEnd);
                    var train = ScoreRange(trades, sessions, fold.TrainingStart, fold.TrainingEnd);
                    var minimum = Math.Max(6, (fold.TrainingSessions + 5) / 6);
                    var qualifies = train.Trades >= minimum &&
                                    early.Trades >= Math.Max(3, minimum / 4) &&
                                    late.Trades >= Math.Max(3, minimum / 4);
                    // Penalize both high peak-to-trough drawdown and dependence
                    // on an unprofitable training half. This does not use OOS.
                    var robustness = Robustness(train, early, late);
                    if (!qualifies) continue;
                    eligible[f]++;
                    var candidate = new Choice(variant, train, trades,
                        robustness, early, late);
                    if (!bestForFold[f].TryGetValue(variant.Kind, out var current) ||
                        BetterRobust(candidate, current))
                        bestForFold[f][variant.Kind] = candidate;
                }

                tested++;
                if (tested % 500 == 0)
                {
                    Console.WriteLine("Deep multi-strategy grid: " +
                                      tested.ToString("N0", Ci) + "/" +
                                      variants.Count.ToString("N0", Ci));
                }
                // The existing ORB simulator caches extensive signal/trade
                // variations. Purge periodically to avoid unbounded memory;
                // the non-ORB cache itself holds at most one entry recipe.
                if (variant.Kind == MultiStrategyKind.Orb && tested % 1200 == 0)
                    OptimizationEngine.ClearCaches();
            }
        }

        var chosenOos = new List<(int Fold, MultiStrategyTrade Trade)>();
        var individualOos = kinds.ToDictionary(k => k,
            _ => new List<MultiStrategyTrade>());
        var individualFoldResults = kinds.ToDictionary(k => k,
            _ => new List<decimal>());
        var summary = new List<string> {
            "DEEP MULTI-STRATEGY ES RESEARCH (NO ORDERS)",
            "Source: dbo.MES_DATA; complete RTH trading dates: " + sessions.Count,
            "Date range: " + Day(sessions[0].Day) + " through " + Day(sessions[^1].Day),
            "Complete overnight windows: " + sessions.Count(x => x.Overnight is not null),
            "Profile: " + profile + "; candidate configurations: " + variants.Count,
            "Training ranks candidates by net P&L adjusted for drawdown and unstable halves.",
            "Each chronological fold ranks ONLY earlier training dates; its later test is OOS for that fold.",
            "Full-sample profit and highest-win-rate winners are IN-SAMPLE diagnostics.",
            "Returns include $10/contract estimated round-trip fees/slippage.",
            "Additional $20/$30 per ES cost scenarios are reported for cost sensitivity.",
            "New strategies trade 1 ES; capped $600 initial risk including $10 cost.",
            "Portfolio positions never overlap and each trading date has <=3 sequential trades.",
            "No new position after realized daily net <= -$1000; gap losses may exceed stop risk.",
            "One-minute OHLC cannot resolve intrabar stop/target ordering; stops win ties.",
            "This date range has been used previously for strategy research: there is no fully untouched holdout.",
            ""
        };

        foreach (var foldIndex in Enumerable.Range(0, folds.Length))
        {
            var fold = folds[foldIndex];
            var winners = bestForFold[foldIndex];
            summary.Add("FOLD " + fold.Number + ": TRAIN " +
                Day(sessions[fold.TrainingStart].Day) + " through " +
                Day(sessions[fold.TrainingEnd - 1].Day) + "; TEST " +
                Day(sessions[fold.TestStart].Day) + " through " +
                Day(sessions[fold.TestEnd - 1].Day));
            summary.Add("Qualified fold candidates: " + eligible[foldIndex]);

            foreach (var kind in kinds)
            {
                if (!winners.TryGetValue(kind, out var chosen))
                {
                    summary.Add(kind + ": no candidate met minimum training/half-trade requirements.");
                    continue;
                }
                var testTrades = new List<MultiStrategyTrade>();
                for (var si = fold.TestStart; si < fold.TestEnd; si++)
                    if (chosen.Trades.TryGetValue(sessions[si].Day, out var trade))
                        testTrades.Add(trade);
                var test = MultiScore.From(testTrades);
                individualOos[kind].AddRange(testTrades);
                individualFoldResults[kind].Add(test.Net);
                var reportLine = "  " + kind + ": config " + chosen.Variant.Id +
                    ", train $" + F(chosen.Score.Net) +
                    " (early $" + F(chosen.Early.Net) +
                    ", late $" + F(chosen.Late.Net) +
                    ") -> next-block OOS $" + F(test.Net) +
                    ", " + test.Trades + " trades";
                Console.WriteLine("Fold " + fold.Number + reportLine);
                summary.Add(reportLine);
            }

            // Search single-strategy, fixed-slot and 16 adaptive policies using
            // only the training history. Adaptive routing uses pre-session data.
            var candidates = new List<MultiPortfolioPolicy>();
            foreach (var kind in winners.Keys)
                candidates.Add(new MultiPortfolioPolicy(MultiPortfolioMode.Single, kind));
            candidates.Add(new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions));
            foreach (var overnight in new[] { 1m, 2m })
            foreach (var orbThreshold in new[] { 2m, 3m })
            foreach (var morning in new[] { 1m, 2m })
            foreach (var afternoon in new[] { 2m, 3m })
                candidates.Add(new MultiPortfolioPolicy(
                    MultiPortfolioMode.AdaptiveRegimes, null,
                    overnight, orbThreshold, morning, afternoon));

            var ranked = new List<(MultiPortfolioPolicy Policy, PortfolioOutcome Training, decimal Robustness)>();
            var mid = fold.TrainingStart + fold.TrainingSessions / 2;
            foreach (var policy in candidates)
            {
                var fullTrain = EvaluatePortfolio(policy, winners, sessions,
                    fold.TrainingStart, fold.TrainingEnd);
                var first = EvaluatePortfolio(policy, winners, sessions,
                    fold.TrainingStart, mid);
                var second = EvaluatePortfolio(policy, winners, sessions,
                    mid, fold.TrainingEnd);
                var robust = Robustness(fullTrain.Score, first.Score, second.Score);
                ranked.Add((policy, fullTrain, robust));
            }
            var minTrades = Math.Max(6, (fold.TrainingSessions + 5) / 6);
            var admissible = ranked.Where(x => x.Training.Score.Trades >= minTrades).ToArray();
            var chosenPolicy = (admissible.Length == 0 ? ranked.ToArray() : admissible)
                .OrderByDescending(x => x.Robustness)
                .ThenByDescending(x => x.Training.Score.Net)
                .ThenBy(x => x.Training.Score.Drawdown)
                .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
                .First();

            var oos = EvaluatePortfolio(chosenPolicy.Policy, winners, sessions,
                fold.TestStart, fold.TestEnd);
            chosenOos.AddRange(oos.Trades.Select(t => (fold.Number, t)));
            var message = "Fold " + fold.Number + " PORTFOLIO: " +
                chosenPolicy.Policy.Description + "; train $" +
                F(chosenPolicy.Training.Score.Net) + "; next-block OOS $" +
                F(oos.Score.Net) + " (" + oos.Score.Trades + " trades)";
            Console.WriteLine(message);
            summary.Add(message);
            summary.Add("");
        }

        // Each strategy's five nonoverlapping NEXT-BLOCK tests are combined
        // without using those returns to choose fold configurations.
        // This is a diagnostic ranking after the research, not a new live rule.
        var oosStrategyScores = new List<(MultiStrategyKind Kind, MultiScore Score)>();
        foreach (var kind in kinds)
        {
            var score = MultiScore.From(individualOos[kind]);
            oosStrategyScores.Add((kind, score));
        }
        summary.Add("STITCHED STANDALONE STRATEGY OOS TOTALS (POST-HOC COMPARISON; not a training rule)");
        foreach (var (kind, score) in oosStrategyScores
                     .OrderByDescending(x => x.Score.Net))
            summary.Add("  " + kind + ": net $" + F(score.Net) +
                        ", " + score.Trades + " trades, " +
                        F(score.WinRate) + "% wins, drawdown $" +
                        F(score.Drawdown) + ", positive folds " +
                        individualFoldResults[kind].Count(x => x > 0m) +
                        "/" + individualFoldResults[kind].Count);
        summary.Add("");

        summary.Add("FULL-SAMPLE STRATEGY WINNERS — ALL IN-SAMPLE; minimum ES trades " +
                    minimumFull);
        foreach (var kind in kinds)
        {
            if (!fullProfit.TryGetValue(kind, out var profit))
            {
                summary.Add("  " + kind + ": no eligible full-data configuration.");
                continue;
            }
            summary.Add("  " + kind + " MAX NET: config " + profit.Variant.Id +
                ", net $" + F(profit.Score.Net) + ", win " +
                F(profit.Score.WinRate) + "%, " + profit.Score.Trades +
                " trades, drawdown $" + F(profit.Score.Drawdown));
            if (fullWinRate.TryGetValue(kind, out var winRate))
            {
                summary.Add("  " + kind + " MAX WIN RATE: config " + winRate.Variant.Id +
                    ", " + F(winRate.Score.WinRate) + "% on " +
                    winRate.Score.Trades + " trades, net $" + F(winRate.Score.Net));
            }
        }

        var oosSorted = chosenOos.OrderBy(x => x.Trade.EntryAt).ToArray();
        var oosScore = MultiScore.From(oosSorted.Select(x => x.Trade));
        summary.Add("");
        summary.Add("STITCHED WALK-FORWARD OOS NET: $" + F(oosScore.Net) +
            "; " + oosScore.Trades + " trades; win rate " +
            F(oosScore.WinRate) + "%; drawdown $" +
            F(oosScore.Drawdown) + "; profit factor " + F(oosScore.ProfitFactor));
        summary.Add("OOS NET IF $20/ES COST: $" + F(SensitiveNet(oosScore, 20m)) +
                    "; IF $30/ES COST: $" + F(SensitiveNet(oosScore, 30m)));
        Console.WriteLine();
        Console.WriteLine("=== DEEP MULTI-STRATEGY WALK-FORWARD RESULTS ===");
        Console.WriteLine("Training-selected combined OOS $" + F(oosScore.Net) +
            ", trades " + oosScore.Trades + ", win rate " +
            F(oosScore.WinRate) + "%, drawdown $" + F(oosScore.Drawdown));
        Console.WriteLine("Cost sensitivity: $20/ES => $" +
            F(SensitiveNet(oosScore, 20m)) +
            "; $30/ES => $" + F(SensitiveNet(oosScore, 30m)));
        Console.WriteLine("Standalone strategy OOS (post-hoc comparison, not a fitted portfolio):");
        foreach (var (kind, score) in oosStrategyScores
                     .OrderByDescending(x => x.Score.Net))
            Console.WriteLine("  " + kind + ": net $" + F(score.Net) +
                ", trades " + score.Trades + ", win rate " + F(score.WinRate) + "%");
        return 0;
    }

    // With >=60 validated sessions, use five expanding-window tests following
    // the first 40% of the dates. Every subsequent day appears in exactly one
    // test block, and no fold's own test dates enter its training selection.
    // A small 25-session synthetic test retains the legacy three folds.
    internal static WalkForwardFold[] CreateResearchFolds(int totalSessions)
    {
        if (totalSessions < 60)
            return WalkForwardFold.Create(totalSessions);
        var firstTraining = totalSessions * 2 / 5;
        var remaining = totalSessions - firstTraining;
        return Enumerable.Range(0, 5)
            .Select(i =>
            {
                var testStart = firstTraining + remaining * i / 5;
                var testEnd = firstTraining + remaining * (i + 1) / 5;
                return new WalkForwardFold(i + 1, 0, testStart, testStart, testEnd);
            }).ToArray();
    }

    // All scores derive ONLY from individual trade outcomes on the requested
    // chronological range. No test-period P&L enters training selection.
    private static MultiScore ScoreRange(
        IReadOnlyDictionary<DateOnly, MultiStrategyTrade> trades,
        IReadOnlyList<MultiSession> sessions, int from, int to)
    {
        var picked = new List<MultiStrategyTrade>();
        for (var i = from; i < to; i++)
            if (trades.TryGetValue(sessions[i].Day, out var item))
                picked.Add(item);
        return MultiScore.From(picked);
    }

    private static decimal Robustness(MultiScore total, MultiScore first, MultiScore second) =>
        total.Net - 0.5m * total.Drawdown -
        0.5m * (Math.Max(0m, -first.Net) + Math.Max(0m, -second.Net));

    private static bool BetterRobust(Choice a, Choice b) =>
        a.Robustness > b.Robustness ||
        a.Robustness == b.Robustness &&
        Better(a.Score, a.Variant.Id, b.Score, b.Variant.Id);

    private static decimal SensitiveNet(MultiScore score, decimal costPerEs) =>
        score.Net - (costPerEs - 10m) * score.Trades;

    private static string WinnerRow(Choice c)
    {
        var s = c.Score;
        return string.Join(",", c.Variant.Kind, c.Variant.Id,
            s.Trades, s.Wins, F(s.WinRate), F(s.Net), F(s.Gross),
            F(s.Costs), F(s.Drawdown), F(s.ProfitFactor), F(s.Average),
            F(SensitiveNet(s, 20m)), F(SensitiveNet(s, 30m)),
            c.Variant.Parameters);
    }

    private static string ScoreRow(
        int fold, string period, MultiStrategyVariant v, MultiScore score,
        MultiScore? early, MultiScore? late, decimal? robustness, bool qualifies) =>
        string.Join(",", fold, period, v.Kind, v.Id, score.Trades, score.Wins,
            F(score.WinRate), F(score.Net), F(score.Gross),
            F(score.Costs), F(score.Drawdown), F(score.ProfitFactor),
            F(score.Average), early is null ? "" : F(early.Net),
            late is null ? "" : F(late.Net),
            robustness is null ? "" : F(robustness.Value),
            qualifies ? "yes" : "no", v.Parameters);

    private static PortfolioOutcome EvaluatePortfolio(
        MultiPortfolioPolicy policy,
        IReadOnlyDictionary<MultiStrategyKind, Choice> winners,
        IReadOnlyList<MultiSession> sessions, int from, int to)
    {
        var chosenTrades = new List<MultiStrategyTrade>();
        var bestBySlot = winners.Values
            .Where(c => c.Score.Net > 0m)
            .GroupBy(c => c.Variant.Slot)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Robustness)
                .ThenByDescending(c => c.Score.Net)
                .ThenBy(c => c.Variant.Id)
                .First().Variant.Kind);

        // In adaptive mode, the best strategy for each slot and regime
        // is fitted on TRAINING only; no same-day P&L decides the route.
        var bestByRegime = winners.Values
            .Where(c => c.Score.Net > 0m)
            .GroupBy(c => (Slot: c.Variant.Slot,
                           Trend: IsTrendStrategy(c.Variant.Kind)))
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(c => c.Robustness)
                .ThenByDescending(c => c.Score.Net)
                .ThenBy(c => c.Variant.Id)
                .First().Variant.Kind);

        void ChooseRegime(List<MultiStrategyKind> wanted,
            MultiStrategySlot slot, bool trend)
        {
            if (bestByRegime.TryGetValue((slot, trend), out var selected))
                wanted.Add(selected);
        }

        for (var i = from; i < to; i++)
        {
            var day = sessions[i];
            var wanted = new List<MultiStrategyKind>();
            switch (policy.Mode)
            {
                case MultiPortfolioMode.Single:
                    if (policy.SingleStrategy is { } one) wanted.Add(one);
                    break;
                case MultiPortfolioMode.FixedSessions:
                    foreach (var slot in new[] { MultiStrategySlot.Overnight,
                                                 MultiStrategySlot.Morning,
                                                 MultiStrategySlot.Afternoon })
                        if (bestBySlot.TryGetValue(slot, out var kind))
                            wanted.Add(kind);
                    break;
                case MultiPortfolioMode.AdaptiveRegimes:
                    if (day.Overnight is not null && day.FirstHourOvernightAtr > 0m)
                        ChooseRegime(wanted, MultiStrategySlot.Overnight,
                            Math.Abs(day.FirstHourOvernightMove) >=
                            policy.NightThreshold * day.FirstHourOvernightAtr);

                    if (day.EarlyAtr > 0m)
                    {
                        var trending = day.OpeningWidth >=
                            policy.OrbThreshold * day.EarlyAtr &&
                            day.EarlyVolumeRatio >= 0.8m ||
                            Math.Abs(day.OpeningMove) >=
                            policy.MorningThreshold * day.EarlyAtr;
                        ChooseRegime(wanted, MultiStrategySlot.Morning, trending);
                    }

                    if (day.AfternoonAtr > 0m)
                        ChooseRegime(wanted, MultiStrategySlot.Afternoon,
                            Math.Abs(day.AfternoonMove) >=
                            policy.AfternoonThreshold * day.AfternoonAtr);
                    break;
            }

            // Candidate selection is based on training scores and features
            // available before the corresponding session starts.
            var candidates = new List<MultiStrategyTrade>();
            foreach (var kind in wanted)
                if (winners.TryGetValue(kind, out var selected) &&
                    selected.Score.Net > 0m &&
                    selected.Trades.TryGetValue(day.Day, out var trade))
                    candidates.Add(trade);

            DateTimeOffset lastExit = DateTimeOffset.MinValue;
            decimal realized = 0m;
            int count = 0;
            foreach (var trade in candidates.OrderBy(x => x.EntryAt))
            {
                if (count >= MaxDailyTrades || realized <= -DailyLossStop) break;
                if (trade.EntryAt < lastExit) continue;
                chosenTrades.Add(trade);
                lastExit = trade.ExitAt;
                realized += trade.Net;
                count++;
            }
        }
        return new PortfolioOutcome(policy, MultiScore.From(chosenTrades), chosenTrades);
    }

    // Directional breakout/extension/retest families are trend-following;
    // bounce, liquidity-sweep, Fibonacci retracement and VWAP reversion
    // are range/reversal candidates. Morning/afternoon are independent.
    private static bool IsTrendStrategy(MultiStrategyKind kind) => kind is
        MultiStrategyKind.Orb or
        MultiStrategyKind.MorningMomentum or MultiStrategyKind.AfternoonMomentum or
        MultiStrategyKind.OvernightMomentum or
        MultiStrategyKind.MorningSupportResistanceBreakout or
        MultiStrategyKind.AfternoonSupportResistanceBreakout or
        MultiStrategyKind.MorningBreakoutRetest or
        MultiStrategyKind.AfternoonBreakoutRetest or
        MultiStrategyKind.MorningFibonacciExtension or
        MultiStrategyKind.AfternoonFibonacciExtension;

    private static bool HigherWinRate(MultiScore a, int idA, MultiScore b, int idB)
    {
        var lhs = (long)a.Wins * b.Trades;
        var rhs = (long)b.Wins * a.Trades;
        return lhs > rhs || lhs == rhs && Better(a, idA, b, idB);
    }

    private static bool Better(MultiScore a, int idA, MultiScore b, int idB) =>
        a.Net > b.Net ||
        a.Net == b.Net && a.Drawdown < b.Drawdown ||
        a.Net == b.Net && a.Drawdown == b.Drawdown && a.Trades > b.Trades ||
        a.Net == b.Net && a.Drawdown == b.Drawdown &&
        a.Trades == b.Trades && idA < idB;

    private static string F(decimal? v) => v is null ? "n/a" : F(v.Value);
    private static string F(decimal v) => v.ToString("0.00", Ci);
    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", Ci);
    private static string Et(DateTimeOffset d) =>
        MarketTime.ToEastern(d).ToString("yyyy-MM-ddTHH:mm:sszzz", Ci);
}
