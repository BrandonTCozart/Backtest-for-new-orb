using System.Globalization;
using System.Collections.Concurrent;
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

public enum MultiPortfolioMode { Single, FixedSessions, AdaptiveRegimes, RegimeAware }

public sealed record MultiPortfolioPolicy(
    MultiPortfolioMode Mode, MultiStrategyKind? SingleStrategy = null,
    decimal NightThreshold = 1m, decimal OrbThreshold = 2m,
    decimal MorningThreshold = 1m, decimal AfternoonThreshold = 2m,
    bool UseFallbacks = false,
    decimal RegimeTrendAtr = 1.5m,
    decimal RegimeMinEfficiency = 0.2m,
    decimal RegimeMaxVolatility = 2m,
    decimal RegimeMinLiquidity = 0.6m)
{
    public string Description => (Mode switch
    {
        MultiPortfolioMode.Single => "Single_" + SingleStrategy,
        MultiPortfolioMode.FixedSessions => "Fixed_best_per_session",
        MultiPortfolioMode.AdaptiveRegimes =>
            "Adaptive_night" + NightThreshold + "_orb" + OrbThreshold +
            "_morning" + MorningThreshold + "_afternoon" + AfternoonThreshold,
        MultiPortfolioMode.RegimeAware =>
            "Regime_v1_trend" + RegimeTrendAtr +
            "_eff" + RegimeMinEfficiency +
            "_vol" + RegimeMaxVolatility + "_liq" + RegimeMinLiquidity,
        _ => throw new ArgumentOutOfRangeException(nameof(Mode))
    }) + (UseFallbacks ? "_fallback" : "");
}

public static class MultiStrategyRunner
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const decimal DailyLossStop = 1000m;
    private const int MaxDailyTrades = 3;

    internal sealed record Choice(
        MultiStrategyVariant Variant, MultiScore Score,
        IReadOnlyDictionary<DateOnly, MultiStrategyTrade> Trades,
        decimal Robustness, MultiScore Early, MultiScore Late);
    internal sealed record PortfolioOutcome(
        MultiPortfolioPolicy Policy, MultiScore Score,
        List<MultiStrategyTrade> Trades, int FallbackTrades);

    public static int Run(
        List<PriceBar> active, List<PriceBar> all, string root,
        MultiSearchProfile profile = MultiSearchProfile.Deep,
        int parallelWorkers = 0) =>
        RunCore(active, all, root, profile, null,
            parallelWorkers: parallelWorkers);

    // The combined pipeline captures the ACTUAL last-fold training winners
    // and routing policy. This avoids re-fitting or stale pinned variant IDs.
    internal static int RunAndCapture(
        List<PriceBar> active, List<PriceBar> all, string root,
        MultiSearchProfile profile,
        Action<HistoricalReplayRunner.FrozenPlan> onSelected,
        bool fitEntireTrainingSample = false,
        string sourceTable = "MES_DATA",
        int parallelWorkers = 0) =>
        RunCore(active, all, root, profile, onSelected,
            fitEntireTrainingSample, sourceTable, parallelWorkers);

    private static int RunCore(
        List<PriceBar> active, List<PriceBar> all, string root,
        MultiSearchProfile profile,
        Action<HistoricalReplayRunner.FrozenPlan>? onSelected,
        bool fitEntireTrainingSample = false,
        string sourceTable = "MES_DATA",
        int parallelWorkers = 0)
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
        if (sessions[0].Day < new DateOnly(2020, 9, 8) ||
            sessions[^1].Day > new DateOnly(2026, 10, 30))
            Console.WriteLine("WARNING: checked-in economic-event calendar does not cover every tested date.");
        Console.WriteLine("Research SQL source: dbo." + sourceTable);
        if (fitEntireTrainingSample)
            Console.WriteLine("Final frozen selection fits ALL validated training-source dates; " +
                "internal walk-forward blocks are diagnostics, not the forward holdout.");

        var days = sessions.Select(x => x.Day).ToHashSet();
        var originalOrb = OptimizationData.Prepare(active, all)
            .Where(x => x.Range is not null && days.Contains(x.Day))
            .OrderBy(x => x.Day).ToArray();
        var folds = CreateResearchFolds(sessions.Count);
        // The optional extra pseudo-fold accumulates scores for the complete
        // historical training sample, without reading the future test table.
        var scoringFolds = fitEntireTrainingSample
            ? folds.Append(new WalkForwardFold(0, 0, sessions.Count,
                sessions.Count, sessions.Count)).ToArray()
            : folds;
        var sessionDays = sessions.Select(s => s.Day).ToArray();
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
        if (parallelWorkers < 0 || parallelWorkers > 32)
            throw new ArgumentOutOfRangeException(nameof(parallelWorkers),
                "Parallel workers must be 0 (automatic) or 1–32.");
        var workers = parallelWorkers == 0
            ? Math.Min(8, Math.Max(1, Environment.ProcessorCount - 1))
            : parallelWorkers;
        Console.WriteLine("Optimizer workers: " + workers +
            " (ORB runs sequentially; other strategies use independent worker caches).");

        var bestForFold = folds.Select(_ => new Dictionary<MultiStrategyKind, Choice>())
            .ToArray();
        var fullProfit = new Dictionary<MultiStrategyKind, Choice>();
        var fullWinRate = new Dictionary<MultiStrategyKind, Choice>();
        var fullRobust = new Dictionary<MultiStrategyKind, Choice>();
        var eligible = new int[folds.Length];
        var minimumFull = Math.Max(10, (sessions.Count + 3) / 4);
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();
        // Keep ORB sequential because its mature OptimizationEngine has shared
        // mutable caches. All other 18 families use thread-local entry/exit
        // caches and can safely evaluate independent variant IDs concurrently.
        // A short lock merges ONLY candidate scores/rankings; the expensive
        // candle simulation and scoring always run outside that lock.
        var selectionLock = new object();
        var progressLock = new object();
        var tested = 0;
        void EvaluateCandidate(MultiStrategyVariant variant)
        {
            var trades = MultiStrategyEngine.Generate(variant, sessions, originalOrb);
            var (full, foldScores) = ScoreCandidateRanges(
                trades, sessionDays, scoringFolds);
            lock (selectionLock)
            {
                if (fitEntireTrainingSample)
                {
                    var whole = foldScores[^1];
                    var minimum = Math.Max(10, (sessions.Count + 11) / 12);
                    if (full.Trades >= minimum &&
                        whole.Early.Trades >= Math.Max(3, minimum / 4) &&
                        whole.Late.Trades >= Math.Max(3, minimum / 4))
                    {
                        var candidate = new Choice(variant, full, trades,
                            Robustness(full, whole.Early, whole.Late),
                            whole.Early, whole.Late);
                        if (!fullRobust.TryGetValue(variant.Kind, out var current) ||
                            BetterRobust(candidate, current))
                            fullRobust[variant.Kind] = candidate;
                    }
                }
                if (full.Trades >= minimumFull)
                {
                    var candidate = new Choice(variant, full, trades,
                        full.Net, full, full);
                    if (!fullProfit.TryGetValue(variant.Kind, out var previous) ||
                        Better(full, variant.Id, previous.Score, previous.Variant.Id))
                        fullProfit[variant.Kind] = candidate;
                    if (!fullWinRate.TryGetValue(variant.Kind, out previous) ||
                        HigherWinRate(full, variant.Id, previous.Score,
                            previous.Variant.Id))
                        fullWinRate[variant.Kind] = candidate;
                }

                for (var f = 0; f < folds.Length; f++)
                {
                    var fold = folds[f];
                    var early = foldScores[f].Early;
                    var late = foldScores[f].Late;
                    var train = foldScores[f].Train;
                    var minimum = Math.Max(6, (fold.TrainingSessions + 5) / 6);
                    var qualifies = train.Trades >= minimum &&
                        early.Trades >= Math.Max(3, minimum / 4) &&
                        late.Trades >= Math.Max(3, minimum / 4);
                    if (!qualifies) continue;
                    eligible[f]++;
                    var candidate = new Choice(variant, train, trades,
                        Robustness(train, early, late), early, late);
                    if (!bestForFold[f].TryGetValue(variant.Kind, out var current) ||
                        BetterRobust(candidate, current))
                        bestForFold[f][variant.Kind] = candidate;
                }
            }

            var completed = Interlocked.Increment(ref tested);
            if (completed % 500 == 0)
                lock (progressLock)
                    Console.WriteLine("Deep multi-strategy grid: " +
                        completed.ToString("N0", Ci) + "/" +
                        variants.Count.ToString("N0", Ci));
            if (variant.Kind == MultiStrategyKind.Orb && completed % 1200 == 0)
                OptimizationEngine.ClearCaches();
        }

        var firstNonOrb = 0;
        while (firstNonOrb < variants.Count &&
               variants[firstNonOrb].Kind == MultiStrategyKind.Orb)
            firstNonOrb++;
        for (var i = 0; i < firstNonOrb; i++)
            EvaluateCandidate(variants[i]);

        if (workers == 1)
        {
            for (var i = firstNonOrb; i < variants.Count; i++)
                EvaluateCandidate(variants[i]);
        }
        else
        {
            // Contiguous chunks favor the per-thread signal/exit cache.
            // Only read-only session data is shared across parallel workers.
            Parallel.ForEach(
                Partitioner.Create(firstNonOrb, variants.Count, 256),
                new ParallelOptions { MaxDegreeOfParallelism = workers },
                range =>
                {
                    for (var i = range.Item1; i < range.Item2; i++)
                        EvaluateCandidate(variants[i]);
                });
        }

        HistoricalReplayRunner.FrozenPlan? lastFoldPlan = null;
        var chosenOos = new List<(int Fold, MultiStrategyTrade Trade)>();
        var bestOriginalRouterOos = new List<MultiStrategyTrade>();
        var bestFallbackRouterOos = new List<MultiStrategyTrade>();
        var fallbackRouterFillCount = 0;
        var initialPortfolioTrades = new List<MultiStrategyTrade>();
        var individualOos = kinds.ToDictionary(k => k,
            _ => new List<MultiStrategyTrade>());
        var individualFoldResults = kinds.ToDictionary(k => k,
            _ => new List<decimal>());
        var summary = new List<string> {
            "DEEP MULTI-STRATEGY ES RESEARCH (NO ORDERS)",
            "Source: dbo." + sourceTable + "; complete RTH trading dates: " + sessions.Count,
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

            // Search single, fixed, legacy adaptive and point-in-time regime
            // policies. All router thresholds are selected on TRAINING only.
            var candidates = new List<MultiPortfolioPolicy>();
            foreach (var kind in winners.Keys)
                candidates.Add(new MultiPortfolioPolicy(MultiPortfolioMode.Single, kind));
            candidates.Add(new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions));
            candidates.Add(new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions,
                UseFallbacks: true));
            foreach (var overnight in new[] { 1m, 2m })
            foreach (var orbThreshold in new[] { 2m, 3m })
            foreach (var morning in new[] { 1m, 2m })
            foreach (var afternoon in new[] { 2m, 3m })
            {
                var basePolicy = new MultiPortfolioPolicy(
                    MultiPortfolioMode.AdaptiveRegimes, null,
                    overnight, orbThreshold, morning, afternoon);
                candidates.Add(basePolicy);
                candidates.Add(basePolicy with { UseFallbacks = true });
            }
            candidates.AddRange(EnumerateRegimePolicies());

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

            var bestRegime = ranked
                .Where(x => x.Policy.Mode == MultiPortfolioMode.RegimeAware)
                .OrderByDescending(x => x.Robustness)
                .ThenByDescending(x => x.Training.Score.Net)
                .ThenBy(x => x.Training.Score.Drawdown)
                .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
                .First();
            if (onSelected is not null && foldIndex == folds.Length - 1)
                lastFoldPlan = HistoricalReplayRunner.FromSelection(
                    sessions[fold.TrainingStart].Day,
                    sessions[fold.TrainingEnd - 1].Day,
                    winners, chosenPolicy.Policy, bestRegime.Policy);

            var oos = EvaluatePortfolio(chosenPolicy.Policy, winners, sessions,
                fold.TestStart, fold.TestEnd);
            var regimeOos = EvaluatePortfolio(bestRegime.Policy, winners,
                sessions, fold.TestStart, fold.TestEnd);
            Console.WriteLine("FOLD_REGIME_COMPARE," + fold.Number + "," +
                bestRegime.Policy.Description + ",train_net=" +
                F(bestRegime.Training.Score.Net) + ",oos_net=" +
                F(regimeOos.Score.Net) + ",oos_trades=" +
                regimeOos.Score.Trades + ",oos_drawdown=" +
                F(regimeOos.Score.Drawdown));
            chosenOos.AddRange(oos.Trades.Select(t => (fold.Number, t)));
            if (foldIndex == 0)
                initialPortfolioTrades.AddRange(chosenPolicy.Training.Trades);
            var message = "Fold " + fold.Number + " PORTFOLIO: " +
                chosenPolicy.Policy.Description + "; train $" +
                F(chosenPolicy.Training.Score.Net) + "; next-block OOS $" +
                F(oos.Score.Net) + " (" + oos.Score.Trades +
                " trades, " + oos.FallbackTrades + " fallback fills)";
            var bestFallback = ranked.Where(x => x.Policy.UseFallbacks)
                .OrderByDescending(x => x.Robustness)
                .ThenByDescending(x => x.Training.Score.Net)
                .ThenBy(x => x.Training.Score.Drawdown)
                .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
                .First();
            var bestOriginal = ranked.Where(x => !x.Policy.UseFallbacks &&
                x.Policy.Mode != MultiPortfolioMode.Single)
                .OrderByDescending(x => x.Robustness)
                .ThenByDescending(x => x.Training.Score.Net)
                .ThenBy(x => x.Training.Score.Drawdown)
                .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
                .First();
            var fallbackOos = EvaluatePortfolio(bestFallback.Policy, winners,
                sessions, fold.TestStart, fold.TestEnd);
            var originalOos = EvaluatePortfolio(bestOriginal.Policy, winners,
                sessions, fold.TestStart, fold.TestEnd);
            bestOriginalRouterOos.AddRange(originalOos.Trades);
            bestFallbackRouterOos.AddRange(fallbackOos.Trades);
            fallbackRouterFillCount += fallbackOos.FallbackTrades;
            Console.WriteLine("Fold " + fold.Number + " ROUTING COMPARISON (training-ranked): " +
                "no fallback $" + F(originalOos.Score.Net) + " (" +
                originalOos.Score.Trades + " trades); fallback $" +
                F(fallbackOos.Score.Net) + " (" +
                fallbackOos.Score.Trades + " trades, " +
                fallbackOos.FallbackTrades + " fallback fills)");
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

        // The first fold's fitted configuration handles the earliest training
        // segment; later dates use that fold's training-selected configuration
        // only for its immediately following OOS block. These two periods are
        // disjoint, so their combined profit covers the full validated history
        // exactly once without summing overlapping expanding training windows.
        var prefixEnd = folds[0].TestStart;
        var fullPeriodScores = new List<(MultiStrategyKind Kind, MultiScore Is,
            MultiScore Oos, MultiScore Total)>();
        foreach (var kind in kinds)
        {
            var prefix = new List<MultiStrategyTrade>();
            if (bestForFold[0].TryGetValue(kind, out var firstChoice))
                for (var si = 0; si < prefixEnd; si++)
                    if (firstChoice.Trades.TryGetValue(sessions[si].Day, out var item))
                        prefix.Add(item);
            var combined = prefix.Concat(individualOos[kind]).ToArray();
            if (combined.Select(t => t.Day).Distinct().Count() != combined.Length)
                throw new InvalidOperationException(
                    "Full-period strategy P&L double-counted a trading date: " + kind);
            var prefixScore = MultiScore.From(prefix);
            var oos = MultiScore.From(individualOos[kind]);
            var total = MultiScore.From(combined);
            if (prefixScore.Net + oos.Net != total.Net)
                throw new InvalidOperationException(
                    "Full-period P&L did not reconcile for " + kind);
            fullPeriodScores.Add((kind, prefixScore, oos, total));
        }

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
        ValidatePortfolioOos(oosSorted.Select(x => x.Trade));
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
        var noFallbackRouterScore = MultiScore.From(bestOriginalRouterOos);
        var withFallbackRouterScore = MultiScore.From(bestFallbackRouterOos);
        Console.WriteLine("Training-selected router comparison (stitched OOS):");
        Console.WriteLine("  No fallback: $" + F(noFallbackRouterScore.Net) +
            ", trades " + noFallbackRouterScore.Trades + ", max drawdown $" +
            F(noFallbackRouterScore.Drawdown));
        Console.WriteLine("  Fallback enabled: $" + F(withFallbackRouterScore.Net) +
            ", trades " + withFallbackRouterScore.Trades + ", fallback fills " +
            fallbackRouterFillCount + ", max drawdown $" +
            F(withFallbackRouterScore.Drawdown));
        Console.WriteLine("  These are independently training-selected best fixed/adaptive routers;");
        Console.WriteLine("  selected portfolio can also be a single-strategy router.");
        Console.WriteLine("Standalone strategy OOS (post-hoc comparison, not a fitted portfolio):");
        foreach (var (kind, score) in oosStrategyScores
                     .OrderByDescending(x => x.Score.Net))
            Console.WriteLine("  " + kind + ": net $" + F(score.Net) +
                ", trades " + score.Trades + ", win rate " + F(score.WinRate) +
                "%, max closed-trade drawdown $" + F(score.Drawdown) +
                ", profitable folds " + individualFoldResults[kind].Count(x => x > 0m) +
                "/" + individualFoldResults[kind].Count);

        Console.WriteLine();
        Console.WriteLine("=== ALL-HISTORY STRATEGY PNL: IN-SAMPLE PREFIX + STITCHED OOS ===");
        Console.WriteLine("Initial in-sample prefix " + Day(sessions[0].Day) + " through " +
            Day(sessions[prefixEnd - 1].Day) + "; later blocks OOS through " +
            Day(sessions[^1].Day) + ".");
        Console.WriteLine("Each date is counted once. Configurations can change at fold boundaries.");
        Console.WriteLine("The all-history total is NOT the full-sample hindsight-optimized best configuration.");
        Console.WriteLine("TOTAL_PNL,strategy,in_sample_prefix_usd,out_of_sample_usd,total_usd,trades,win_rate_pct,max_closed_trade_drawdown_usd");
        foreach (var (kind, initial, oos, total) in fullPeriodScores
                     .OrderByDescending(x => x.Total.Net))
            Console.WriteLine("TOTAL_PNL," + kind + "," + F(initial.Net) + "," +
                F(oos.Net) + "," + F(total.Net) + "," + total.Trades + "," +
                F(total.WinRate) + "," + F(total.Drawdown));
        var combinedPortfolio = MultiScore.From(initialPortfolioTrades.Concat(
            oosSorted.Select(x => x.Trade)));
        var initialPortfolioScore = MultiScore.From(initialPortfolioTrades);
        if (combinedPortfolio.Net != initialPortfolioScore.Net + oosScore.Net)
            throw new InvalidOperationException(
                "All-history selected portfolio P&L does not reconcile.");
        Console.WriteLine("TOTAL_PNL,SelectedPortfolio," +
            F(initialPortfolioScore.Net) + "," + F(oosScore.Net) + "," +
            F(combinedPortfolio.Net) + "," + combinedPortfolio.Trades + "," +
            F(combinedPortfolio.WinRate) + "," +
            F(combinedPortfolio.Drawdown));

        // The weekly output uses exclusively nonoverlapping, training-selected
        // OOS trades. Calendar grouping is Monday-Sunday in ET, assigning
        // overnight P&L to its associated regular trading-session date.
        // Zero-trade weeks are explicit so chart series always line up.
        var weekly = BuildWeeklyOosRows(individualOos,
            oosSorted.Select(x => x.Trade).ToArray(),
            sessions[folds[0].TestStart].Day,
            sessions[folds[^1].TestEnd - 1].Day);
        Console.WriteLine();
        Console.WriteLine("=== WEEKLY STANDALONE STRATEGY AND PORTFOLIO OOS PNL (USD) ===");
        Console.WriteLine("Weeks are Monday-Sunday ET; profits include $10/ES estimated round-trip costs.");
        Console.WriteLine("Each strategy switches only at walk-forward fold boundaries; weeks without trades have net 0.");
        Console.WriteLine("WEEKLY_OOS,week_start_et,week_end_et,strategy,net_usd,cumulative_net_usd,trades");
        foreach (var week in weekly)
            Console.WriteLine("WEEKLY_OOS," + Day(week.WeekStart) + "," +
                Day(week.WeekEnd) + "," + week.Strategy + "," +
                F(week.Net) + "," + F(week.Cumulative) + "," + week.Trades);

        Console.WriteLine();
        Console.WriteLine("Full-sample MAX NET configurations (IN-SAMPLE diagnostics only):");
        foreach (var kind in kinds)
            if (fullProfit.TryGetValue(kind, out var selected))
                Console.WriteLine("  " + kind + ": config " + selected.Variant.Id +
                    ", net $" + F(selected.Score.Net) +
                    ", win rate " + F(selected.Score.WinRate) +
                    "%, drawdown $" + F(selected.Score.Drawdown) +
                    ", trades " + selected.Score.Trades +
                    ", settings: " + selected.Variant.Parameters);
        Console.WriteLine("Console-only reporting: no CSV or text reports created; existing files are untouched.");
        if (onSelected is not null)
        {
            if (fitEntireTrainingSample)
            {
                var missing = kinds.Where(k => !fullRobust.ContainsKey(k)).ToArray();
                if (missing.Length > 0)
                    throw new InvalidOperationException(
                        "No eligible full-history training configuration for: " +
                        string.Join(", ", missing) +
                        ". Cannot silently skip a family or use forward data for selection.");
                var (policy, bestRegime) = SelectTrainingRouter(
                    fullRobust, sessions, 0, sessions.Count);
                Console.WriteLine();
                Console.WriteLine("=== FULL-HISTORICAL-TRAINING FINAL SELECTION (FROZEN) ===");
                Console.WriteLine("Training: dbo." + sourceTable + ", " +
                    Day(sessions[0].Day) + " through " + Day(sessions[^1].Day) +
                    " (" + sessions.Count + " validated days)");
                Console.WriteLine("Router selected on historical training only: " +
                    policy.Description);
                Console.WriteLine("Best regime-aware comparison router (also frozen " +
                    "from training only): " + bestRegime.Description);
                foreach (var kind in kinds)
                {
                    var selected = fullRobust[kind];
                    Console.WriteLine("TRAIN_FROZEN," + kind + ",config=" +
                        selected.Variant.Id + ",train_net=" + F(selected.Score.Net) +
                        ",train_robustness=" + F(selected.Robustness) +
                        ",trades=" + selected.Score.Trades +
                        ",settings=" + selected.Variant.Parameters);
                }
                onSelected(HistoricalReplayRunner.FromSelection(
                    sessions[0].Day, sessions[^1].Day, fullRobust,
                    policy, bestRegime));
            }
            else
                onSelected(lastFoldPlan ?? throw new InvalidOperationException(
                    "No last-fold plan was selected for historical replay."));
        }
        return 0;
    }

    // The same router choices and robustness ordering used in each
    // chronological training fold, now fitted once on the COMPLETE historical
    // training sample. Evaluation on the later table is strictly downstream.
    private static (MultiPortfolioPolicy Selected, MultiPortfolioPolicy BestRegime)
        SelectTrainingRouter(
        IReadOnlyDictionary<MultiStrategyKind, Choice> winners,
        IReadOnlyList<MultiSession> sessions, int from, int to)
    {
        var policies = new List<MultiPortfolioPolicy>();
        foreach (var kind in winners.Keys)
            policies.Add(new MultiPortfolioPolicy(MultiPortfolioMode.Single, kind));
        policies.Add(new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions));
        policies.Add(new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions,
            UseFallbacks: true));
        foreach (var overnight in new[] { 1m, 2m })
        foreach (var orb in new[] { 2m, 3m })
        foreach (var morning in new[] { 1m, 2m })
        foreach (var afternoon in new[] { 2m, 3m })
        {
            var basis = new MultiPortfolioPolicy(MultiPortfolioMode.AdaptiveRegimes,
                null, overnight, orb, morning, afternoon);
            policies.Add(basis);
            policies.Add(basis with { UseFallbacks = true });
        }
        policies.AddRange(EnumerateRegimePolicies());

        var midpoint = from + (to - from) / 2;
        var scored = policies.Select(policy =>
        {
            var whole = EvaluatePortfolio(policy, winners, sessions, from, to).Score;
            var first = EvaluatePortfolio(policy, winners, sessions, from, midpoint).Score;
            var last = EvaluatePortfolio(policy, winners, sessions, midpoint, to).Score;
            return (Policy: policy, Score: whole,
                Robustness: Robustness(whole, first, last));
        }).ToArray();
        var minimum = Math.Max(6, (to - from + 5) / 6);
        var admissible = scored.Where(x => x.Score.Trades >= minimum).ToArray();
        var ranked = (admissible.Length == 0 ? scored : admissible)
            .OrderByDescending(x => x.Robustness)
            .ThenByDescending(x => x.Score.Net)
            .ThenBy(x => x.Score.Drawdown)
            .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
            .ToArray();
        var bestRegime = scored
            .Where(x => x.Policy.Mode == MultiPortfolioMode.RegimeAware &&
                x.Score.Trades >= minimum)
            .OrderByDescending(x => x.Robustness)
            .ThenByDescending(x => x.Score.Net)
            .ThenBy(x => x.Score.Drawdown)
            .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
            .FirstOrDefault();
        // If strict no-trade settings produce fewer qualifying trades than
        // the floor, retain the strongest regime option only as a diagnostic
        // instead of silently loosening that minimum for the main selection.
        if (bestRegime.Policy is null)
            bestRegime = scored
                .Where(x => x.Policy.Mode == MultiPortfolioMode.RegimeAware)
                .OrderByDescending(x => x.Robustness)
                .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
                .First();
        return (ranked[0].Policy, bestRegime.Policy);
    }

    // The four regime dimensions are intentionally coarse: trying millions
    // of routing thresholds would make the regime layer itself overfit.
    // These settings are frozen when training ends; forward data NEVER ranks.
    private static IEnumerable<MultiPortfolioPolicy> EnumerateRegimePolicies()
    {
        foreach (var trend in new[] { 1.25m, 1.75m })
        foreach (var efficiency in new[] { 0.2m, 0.35m })
        foreach (var volatility in new[] { 1.75m, 2.5m })
        foreach (var liquidity in new[] { 0.5m, 0.75m })
        {
            var candidate = new MultiPortfolioPolicy(
                MultiPortfolioMode.RegimeAware,
                RegimeTrendAtr: trend,
                RegimeMinEfficiency: efficiency,
                RegimeMaxVolatility: volatility,
                RegimeMinLiquidity: liquidity);
            yield return candidate;
            yield return candidate with { UseFallbacks = true };
        }
    }

    // Validate portfolio invariants directly on the stitched OOS trades.
    // Previously these checks relied on rereading a generated trades CSV.
    private static void ValidatePortfolioOos(IEnumerable<MultiStrategyTrade> trades)
    {
        foreach (var group in trades.GroupBy(t => t.Day))
        {
            DateTimeOffset lastExit = DateTimeOffset.MinValue;
            int count = 0;
            foreach (var trade in group.OrderBy(t => t.EntryAt))
            {
                if (trade.ExitAt <= trade.EntryAt || trade.EntryAt < lastExit)
                    throw new InvalidOperationException(
                        "Overlapping or invalid OOS portfolio positions on " + group.Key);
                lastExit = trade.ExitAt;
                count++;
            }
            if (count > MaxDailyTrades)
                throw new InvalidOperationException(
                    "Too many OOS portfolio positions on " + group.Key);
        }
    }

    internal sealed record WeeklyOosRow(
        DateOnly WeekStart, DateOnly WeekEnd, string Strategy,
        decimal Net, decimal Cumulative, int Trades);

    internal static IReadOnlyList<WeeklyOosRow> BuildWeeklyOosRows(
        IReadOnlyDictionary<MultiStrategyKind, List<MultiStrategyTrade>> individualOos,
        IReadOnlyList<MultiStrategyTrade> selectedPortfolioOos,
        DateOnly firstTestDay, DateOnly lastTestDay)
    {
        if (lastTestDay < firstTestDay)
            throw new ArgumentOutOfRangeException(nameof(lastTestDay));
        var rows = new List<WeeklyOosRow>();
        var firstMonday = MondayOf(firstTestDay);
        var lastMonday = MondayOf(lastTestDay);

        void Append(string name, IEnumerable<MultiStrategyTrade> trades)
        {
            var totals = trades
                .Where(t => t.Day >= firstTestDay && t.Day <= lastTestDay)
                .GroupBy(t => MondayOf(t.Day))
                .ToDictionary(g => g.Key,
                    g => (Net: g.Sum(t => t.Net), Trades: g.Count()));
            decimal cumulative = 0m;
            for (var week = firstMonday; week <= lastMonday; week = week.AddDays(7))
            {
                totals.TryGetValue(week, out var value);
                cumulative += value.Net;
                rows.Add(new WeeklyOosRow(week, week.AddDays(6),
                    name, value.Net, cumulative, value.Trades));
            }
        }

        foreach (var kind in Enum.GetValues<MultiStrategyKind>())
            Append(kind.ToString(), individualOos[kind]);
        Append("SelectedPortfolio", selectedPortfolioOos);
        return rows;
    }

    private static DateOnly MondayOf(DateOnly date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

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

    // Same exact aggregation as MultiScore.From, without repeatedly sorting
    // already chronological, at-most-one-trade-per-day strategy results.
    // Each candidate is scanned ONCE and each fold's training, early and late
    // streams gets its own peak/drawdown accumulator.
    private struct ScoreAccumulator
    {
        private int _trades, _wins;
        private decimal _net, _gross, _costs, _peak, _drawdown, _gains, _losses;
        public void Add(MultiStrategyTrade trade)
        {
            ++_trades;
            var net = trade.Net;
            _net += net;
            _gross += trade.Gross;
            _costs += trade.Costs;
            if (trade.IsWin) { ++_wins; _gains += net; }
            else _losses -= net;
            _peak = Math.Max(_peak, _net);
            _drawdown = Math.Max(_drawdown, _peak - _net);
        }
        public MultiScore Finish() => new(_trades, _wins, _net, _gross, _costs,
            _drawdown, _losses > 0m ? _gains / _losses : null);
    }

    internal static (MultiScore Full, (MultiScore Early, MultiScore Late,
        MultiScore Train)[] Folds) ScoreCandidateRanges(
        IReadOnlyDictionary<DateOnly, MultiStrategyTrade> trades,
        IReadOnlyList<DateOnly> sessionDays, IReadOnlyList<WalkForwardFold> folds)
    {
        var full = new ScoreAccumulator();
        var early = new ScoreAccumulator[folds.Count];
        var late = new ScoreAccumulator[folds.Count];
        var training = new ScoreAccumulator[folds.Count];
        var middle = folds.Select(f => f.TrainingStart +
            f.TrainingSessions / 2).ToArray();
        for (var i = 0; i < sessionDays.Count; i++)
        {
            if (!trades.TryGetValue(sessionDays[i], out var trade)) continue;
            full.Add(trade);
            for (var f = 0; f < folds.Count; f++)
            {
                var fold = folds[f];
                if (i < fold.TrainingStart || i >= fold.TrainingEnd) continue;
                training[f].Add(trade);
                if (i < middle[f]) early[f].Add(trade);
                else late[f].Add(trade);
            }
        }
        var scores = new (MultiScore Early, MultiScore Late, MultiScore Train)[folds.Count];
        for (var f = 0; f < folds.Count; f++)
            scores[f] = (early[f].Finish(), late[f].Finish(), training[f].Finish());
        return (full.Finish(), scores);
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

    // Ranked candidate lists and routing thresholds are fitted on TRAINING only.
    // In fallback mode, the first *chronologically available* entry in a slot
    // wins; when two signals enter together, the training rank breaks the tie.
    // Waiting until day-end to see that the primary never signalled, then
    // retroactively taking an earlier fallback, would be lookahead.
    internal static PortfolioOutcome EvaluatePortfolio(
        MultiPortfolioPolicy policy,
        IReadOnlyDictionary<MultiStrategyKind, Choice> winners,
        IReadOnlyList<MultiSession> sessions, int from, int to)
    {
        var chosenTrades = new List<MultiStrategyTrade>();
        var fallbackFills = 0;
        var rankedBySlot = winners.Values
            .Where(c => c.Score.Net > 0m)
            .GroupBy(c => c.Variant.Slot)
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(c => c.Robustness)
                .ThenByDescending(c => c.Score.Net)
                .ThenBy(c => c.Variant.Id)
                .Select(c => c.Variant.Kind).ToArray());

        // Adaptive primary strategy is chosen from the relevant trend/reversal
        // training cohort, using only features available at the routing time.
        var bestByRegime = winners.Values
            .Where(c => c.Score.Net > 0m)
            .GroupBy(c => (Slot: c.Variant.Slot,
                           Trend: IsTrendStrategy(c.Variant.Kind)))
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(c => c.Robustness)
                .ThenByDescending(c => c.Score.Net)
                .ThenBy(c => c.Variant.Id)
                .First().Variant.Kind);

        for (var i = from; i < to; i++)
        {
            var day = sessions[i];
            var preferred = new Dictionary<MultiStrategySlot, MultiStrategyKind>();
            void ChooseRegime(MultiStrategySlot slot, bool trend)
            {
                if (bestByRegime.TryGetValue((slot, trend), out var selected))
                    preferred[slot] = selected;
                else if (policy.UseFallbacks &&
                         rankedBySlot.TryGetValue(slot, out var choices) &&
                         choices.Length > 0)
                    preferred[slot] = choices[0];
            }

            switch (policy.Mode)
            {
                case MultiPortfolioMode.Single:
                    if (policy.SingleStrategy is { } kind &&
                        winners.TryGetValue(kind, out var single) &&
                        single.Score.Net > 0m)
                        preferred[single.Variant.Slot] = kind;
                    break;
                case MultiPortfolioMode.FixedSessions:
                    foreach (var (slot, kinds) in rankedBySlot)
                        preferred[slot] = kinds[0];
                    break;
                case MultiPortfolioMode.RegimeAware:
                {
                    var thresholds = new RegimeThresholds(
                        policy.RegimeTrendAtr, policy.RegimeMinEfficiency,
                        policy.RegimeMaxVolatility, policy.RegimeMinLiquidity);
                    foreach (var slot in Enum.GetValues<MultiStrategySlot>())
                    {
                        var state = MarketRegimeEngine.Classify(
                            MarketRegimeEngine.ForSlot(day, slot), thresholds);
                        // Do not force a trade on uncertain, unusually volatile,
                        // thinly traded or insufficient-reference sessions.
                        if (state == MarketRegime.Trend)
                            ChooseRegime(slot, trend: true);
                        else if (state == MarketRegime.Range)
                            ChooseRegime(slot, trend: false);
                    }
                    break;
                }
                case MultiPortfolioMode.AdaptiveRegimes:
                    if (day.Overnight is not null && day.FirstHourOvernightAtr > 0m)
                        ChooseRegime(MultiStrategySlot.Overnight,
                            Math.Abs(day.FirstHourOvernightMove) >=
                            policy.NightThreshold * day.FirstHourOvernightAtr);

                    if (day.EarlyAtr > 0m)
                    {
                        var trending = (day.OpeningWidth >=
                            policy.OrbThreshold * day.EarlyAtr &&
                            day.EarlyVolumeRatio >= 0.8m) ||
                            Math.Abs(day.OpeningMove) >=
                            policy.MorningThreshold * day.EarlyAtr;
                        ChooseRegime(MultiStrategySlot.Morning, trending);
                    }

                    if (day.AfternoonAtr > 0m)
                        ChooseRegime(MultiStrategySlot.Afternoon,
                            Math.Abs(day.AfternoonMove) >=
                            policy.AfternoonThreshold * day.AfternoonAtr);
                    break;
            }

            var candidates = new List<(MultiStrategyTrade Trade, bool Fallback)>();
            foreach (var (slot, primary) in preferred)
            {
                // Single-policy evaluation never uses other strategies.
                // A non-fallback policy evaluates only the primary.
                var ranked = new List<MultiStrategyKind> { primary };
                if (policy.UseFallbacks &&
                    policy.Mode != MultiPortfolioMode.Single &&
                    rankedBySlot.TryGetValue(slot, out var options))
                    ranked.AddRange(options.Where(kind => kind != primary));

                var available = ranked.Select((kind, priority) =>
                    (Kind: kind, Priority: priority))
                    .Where(x => winners.TryGetValue(x.Kind, out var selected) &&
                        selected.Score.Net > 0m &&
                        selected.Trades.ContainsKey(day.Day))
                    .Select(x => (Trade: winners[x.Kind].Trades[day.Day],
                                  x.Priority))
                    .Where(x => policy.Mode != MultiPortfolioMode.RegimeAware ||
                        x.Trade.EntryAt >= MarketRegimeEngine.ForSlot(day, slot)!.AvailableAt)
                    .OrderBy(x => x.Trade.EntryAt)
                    .ThenBy(x => x.Priority)
                    .FirstOrDefault();
                if (available.Trade is not null)
                    candidates.Add((available.Trade, available.Priority > 0));
            }

            DateTimeOffset lastExit = DateTimeOffset.MinValue;
            decimal realized = 0m;
            int count = 0;
            foreach (var candidate in candidates.OrderBy(x => x.Trade.EntryAt))
            {
                if (count >= MaxDailyTrades || realized <= -DailyLossStop) break;
                var trade = candidate.Trade;
                if (trade.EntryAt < lastExit) continue;
                chosenTrades.Add(trade);
                if (candidate.Fallback) fallbackFills++;
                lastExit = trade.ExitAt;
                realized += trade.Net;
                count++;
            }
        }
        return new PortfolioOutcome(policy, MultiScore.From(chosenTrades),
            chosenTrades, fallbackFills);
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
