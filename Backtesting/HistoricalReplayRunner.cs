using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Replays frozen training-selected variants against a separate SQL period.
/// Supports recent-to-old diagnostics and older-to-newer forward holdout tests.
/// </summary>
public static class HistoricalReplayRunner
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly DateOnly CalendarStart = new(2025, 10, 6);

    // Captured from the user's five-fold 2025-2026 MES_DATA walk-forward:
    // fold 5's training-selected strategy configuration for each family.
    // The selected portfolio policy in that fold was Fixed_best_per_session,
    // with fallback OFF. These are NOT full-sample hindsight winners.
    private static readonly IReadOnlyDictionary<MultiStrategyKind, int> LastFoldIds =
        new Dictionary<MultiStrategyKind, int>
        {
            [MultiStrategyKind.Orb] = 16184,
            [MultiStrategyKind.MorningMomentum] = 24958,
            [MultiStrategyKind.MorningVwapReversion] = 30526,
            [MultiStrategyKind.AfternoonMomentum] = 41064,
            [MultiStrategyKind.AfternoonVwapReversion] = 45056,
            [MultiStrategyKind.OvernightMomentum] = 50588,
            [MultiStrategyKind.OvernightReversion] = 58095,
            [MultiStrategyKind.MorningSupportResistanceBounce] = 60279,
            [MultiStrategyKind.AfternoonSupportResistanceBounce] = 62431,
            [MultiStrategyKind.MorningSupportResistanceBreakout] = 63398,
            [MultiStrategyKind.AfternoonSupportResistanceBreakout] = 64754,
            [MultiStrategyKind.MorningBreakoutRetest] = 65078,
            [MultiStrategyKind.AfternoonBreakoutRetest] = 68211,
            [MultiStrategyKind.MorningFibonacciRetracement] = 69664,
            [MultiStrategyKind.AfternoonFibonacciRetracement] = 70822,
            [MultiStrategyKind.MorningFibonacciExtension] = 71656,
            [MultiStrategyKind.AfternoonFibonacciExtension] = 72242,
            [MultiStrategyKind.MorningLiquiditySweep] = 72471,
            [MultiStrategyKind.AfternoonLiquiditySweep] = 74321
        };

    internal sealed record FrozenChoice(
        MultiStrategyVariant Variant, MultiScore Train, MultiScore Early,
        MultiScore Late, decimal Robustness);

    internal sealed record FrozenPlan(
        DateOnly TrainingFrom, DateOnly TrainingThrough,
        IReadOnlyDictionary<MultiStrategyKind, FrozenChoice> Choices,
        MultiPortfolioPolicy? SelectedPolicy = null,
        MultiPortfolioPolicy? RegimeComparisonPolicy = null,
        AdaptiveConfigurationModel? AdaptiveExperts = null);

    // Called by the sequential run ONLY after fold 5's optimizer has finished
    // selecting with MES_DATA training P&L. No historical P&L enters this plan.
    internal static FrozenPlan FromSelection(
        DateOnly trainingFrom, DateOnly trainingThrough,
        IReadOnlyDictionary<MultiStrategyKind, MultiStrategyRunner.Choice> chosen,
        MultiPortfolioPolicy selectedPolicy,
        MultiPortfolioPolicy? regimeComparison = null,
        AdaptiveConfigurationModel? adaptiveExperts = null)
    {
        var selected = chosen.Keys.OrderBy(k => k).ToArray();
        var all = Enum.GetValues<MultiStrategyKind>();
        var focused = MultiStrategyScopeSelection.FocusedKinds.OrderBy(k => k).ToArray();
        if (!selected.SequenceEqual(all) && !selected.SequenceEqual(focused))
            throw new InvalidOperationException(
                "Frozen replay requires an exact full (19) or focused (5) " +
                "training-selected strategy set. Missing winners may not be " +
                "silently replaced with stale configuration IDs.");
        return new FrozenPlan(trainingFrom, trainingThrough,
            chosen.ToDictionary(x => x.Key, x => new FrozenChoice(
                x.Value.Variant, x.Value.Score, x.Value.Early,
                x.Value.Late, x.Value.Robustness)), selectedPolicy,
            regimeComparison, adaptiveExperts);
    }

    // Default run: (1) freshly re-optimize and walk-forward test MES_DATA,
    // (2) reuse the final training-selected variants and router for a clean
    // frozen historical evaluation on MES_DATA_2020_2025.
    public static async Task<int> RunSequentialAsync(
        string? connectionString, string symbol, string timeZone,
        MultiSearchProfile profile, string root, int parallelWorkers = 0,
        MultiStrategyScope strategyScope = MultiStrategyScope.Focused,
        AdaptiveConfigurationMode configurationMode =
            AdaptiveConfigurationMode.AdaptiveCandles)
    {
        Console.WriteLine("=== COMBINED RESEARCH + HISTORICAL VALIDATION ===");
        Console.WriteLine("Step 1/2: walk-forward optimize dbo.MES_DATA using " +
            profile + " with " + strategyScope + " strategy scope.");
        var recent = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timeZone, tableName: "MES_DATA");
        FrozenPlan? selected = null;
        var status = MultiStrategyRunner.RunAndCapture(
            recent.ActiveBars, recent.AllContractBars, root, profile,
            plan => selected = plan, parallelWorkers: parallelWorkers,
            strategyScope: strategyScope,
            configurationMode: configurationMode);
        if (status != 0) return status;
        if (selected is null)
            throw new InvalidOperationException("No training-selected historical plan.");
        // Old candle arrays and signal caches need not remain in memory while
        // reading the much larger historical SQL source.
        recent = null!;
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();

        Console.WriteLine();
        Console.WriteLine("Step 2/2: frozen replay on dbo.MES_DATA_2020_2025.");
        Console.WriteLine("Every strategy variant AND the router policy come from " +
                          "the new last-fold MES_DATA training selection.");
        Console.WriteLine("Older-table outcomes never revise these choices.");
        var historical = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timeZone, tableName: "MES_DATA_2020_2025");
        return Replay(selected, historical.ActiveBars, historical.AllContractBars);
    }

    // Optimize all available earlier SQL candles, then freeze selections
    // before loading any of the later, strictly forward test data.
    public static async Task<int> RunHistoricalThenRecentAsync(
        string? connectionString, string symbol, string timeZone,
        MultiSearchProfile profile, string root, int parallelWorkers = 0,
        MultiStrategyScope strategyScope = MultiStrategyScope.Focused,
        AdaptiveConfigurationMode configurationMode =
            AdaptiveConfigurationMode.AdaptiveCandles)
    {
        Console.WriteLine("=== HISTORICAL TRAINING -> RECENT FORWARD TEST (NO LIVE ORDERS) ===");
        Console.WriteLine("Step 1/2: " + profile + "-grid " + strategyScope +
            " training on dbo.MES_DATA_2020_2025.");
        var historical = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timeZone, tableName: "MES_DATA_2020_2025");
        if (historical.ActiveBars.Count == 0)
            throw new InvalidOperationException(
                "Historical training table contains no active ES candles.");
        var first = DateOnly.FromDateTime(
            MarketTime.ToEastern(historical.ActiveBars[0].T).Date);
        var last = DateOnly.FromDateTime(
            MarketTime.ToEastern(historical.ActiveBars[^1].T).Date);
        ValidateHistoricalReleaseCoverage(first, last);
        FrozenPlan? selected = null;
        var status = MultiStrategyRunner.RunAndCapture(
            historical.ActiveBars, historical.AllContractBars, root, profile,
            plan => selected = plan, fitEntireTrainingSample: true,
            sourceTable: "MES_DATA_2020_2025",
            parallelWorkers: parallelWorkers,
            strategyScope: strategyScope,
            configurationMode: configurationMode);
        if (status != 0) return status;
        if (selected is null)
            throw new InvalidOperationException(
                "No full-history training-selected configuration set was produced.");

        historical = null!;
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();
        Console.WriteLine();
        Console.WriteLine("Step 2/2: frozen forward evaluation on dbo.MES_DATA (2025-2026).");
        Console.WriteLine("No forward-test outcome changes strategy variants or routing.");
        var recent = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timeZone, tableName: "MES_DATA");
        return Replay(selected, recent.ActiveBars, recent.AllContractBars,
            forwardHoldout: true);
    }

    public static async Task<int> RunAsync(
        string? connectionString, string symbol, string timeZone,
        MultiSearchProfile profile)
    {
        if (profile != MultiSearchProfile.Deep)
            throw new ArgumentException(
                "HistoricalReplay requires Backtest:SearchProfile=Deep because " +
                "the pinned configurations use Deep-grid variant IDs.");

        Console.WriteLine("=== HISTORICAL FROZEN-CONFIGURATION REPLAY ===");
        Console.WriteLine("Original configuration source: dbo.MES_DATA, latest (fifth) walk-forward fold.");
        Console.WriteLine("Historical evaluation source: dbo.MES_DATA_2020_2025.");
        Console.WriteLine("No search or configuration selection uses older-table P&L.");

        // Extract and retain only 19 training scores/variants, not old trades.
        // The large MES_DATA candle lists become collectible before the new
        // historical SQL table is loaded.
        var plan = await LoadFrozenPlanAsync(connectionString, symbol, timeZone);

        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();
        Console.WriteLine("Reading frozen-strategy historical replay candles from dbo.MES_DATA_2020_2025...");
        var historical = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timeZone, tableName: "MES_DATA_2020_2025");
        return Replay(plan, historical.ActiveBars, historical.AllContractBars);
    }

    private static async Task<FrozenPlan> LoadFrozenPlanAsync(
        string? connectionString, string symbol, string timeZone)
    {
        Console.WriteLine("Reading recent baseline candles from dbo.MES_DATA...");
        var recent = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timeZone, tableName: "MES_DATA");
        return BuildPlan(recent.ActiveBars, recent.AllContractBars);
    }

    internal static FrozenPlan BuildPlan(
        IReadOnlyList<PriceBar> active, IReadOnlyList<PriceBar> all)
    {
        var sessions = MultiStrategyData.Build(active, all, "unused");
        if (sessions.Count < 20)
            throw new InvalidOperationException(
                "Recent MES_DATA must contain at least 20 complete RTH sessions " +
                "to reproduce the original fold's training ranking.");
        var folds = MultiStrategyRunner.CreateResearchFolds(sessions.Count);
        var final = folds[^1];
        var variants = ResolvePinnedVariants();

        var validDays = sessions.Select(s => s.Day).ToHashSet();
        var orb = OptimizationData.Prepare(active.ToList(), all.ToList())
            .Where(s => s.Range is not null && validDays.Contains(s.Day))
            .OrderBy(s => s.Day).ToArray();
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();

        var winners = new Dictionary<MultiStrategyKind, FrozenChoice>();
        foreach (var (kind, variant) in variants)
        {
            var trades = MultiStrategyEngine.Generate(variant, sessions, orb);
            MultiScore Score(int start, int end)
            {
                var picked = new List<MultiStrategyTrade>();
                for (var i = start; i < end; i++)
                    if (trades.TryGetValue(sessions[i].Day, out var trade))
                        picked.Add(trade);
                return MultiScore.From(picked);
            }

            var mid = final.TrainingStart + final.TrainingSessions / 2;
            var first = Score(final.TrainingStart, mid);
            var second = Score(mid, final.TrainingEnd);
            var train = Score(final.TrainingStart, final.TrainingEnd);
            var robust = train.Net - 0.5m * train.Drawdown -
                0.5m * (Math.Max(0m, -first.Net) +
                        Math.Max(0m, -second.Net));
            winners.Add(kind, new FrozenChoice(variant, train, first, second, robust));
        }

        return new FrozenPlan(
            sessions[final.TrainingStart].Day,
            sessions[final.TrainingEnd - 1].Day, winners);
    }

    private static Dictionary<MultiStrategyKind, MultiStrategyVariant> ResolvePinnedVariants()
    {
        var deep = MultiStrategyEngine.EnumerateVariants(MultiSearchProfile.Deep);
        var byId = deep.ToDictionary(v => v.Id);
        var kinds = Enum.GetValues<MultiStrategyKind>();
        if (LastFoldIds.Count != kinds.Length)
            throw new InvalidOperationException("Pinned replay preset must include every strategy.");
        var result = new Dictionary<MultiStrategyKind, MultiStrategyVariant>();
        foreach (var kind in kinds)
        {
            if (!LastFoldIds.TryGetValue(kind, out var id) ||
                !byId.TryGetValue(id, out var variant) || variant.Kind != kind)
                throw new InvalidOperationException(
                    $"Historical replay pinned config #{id} does not match {kind}; " +
                    "the Deep grid may have changed. Rebuild the preset from a new fold.");
            result.Add(kind, variant);
        }
        return result;
    }

    internal static IReadOnlyDictionary<MultiStrategyKind, int> PinnedIds => LastFoldIds;

    internal static int Replay(
        FrozenPlan plan, IReadOnlyList<PriceBar> active,
        IReadOnlyList<PriceBar> all, bool forwardHoldout = false)
    {
        var sessions = MultiStrategyData.Build(active, all, "unused");
        if (sessions.Count == 0)
        {
            Console.Error.WriteLine("No complete 390-minute RTH sessions in " +
                (forwardHoldout ? "MES_DATA." : "MES_DATA_2020_2025."));
            return 2;
        }

        // A forward holdout may not reuse even one training date.
        if (forwardHoldout && sessions[0].Day <= plan.TrainingThrough)
            throw new InvalidOperationException(
                "Forward test overlaps fitted training dates: " +
                Day(sessions[0].Day) + " <= " + Day(plan.TrainingThrough));
        if (forwardHoldout && (sessions[0].Day < CalendarStart ||
            sessions[^1].Day > new DateOnly(2026, 10, 30)))
            throw new InvalidOperationException(
                "Forward evaluation extends beyond checked-in 2025-2026 event calendar.");

        Console.WriteLine();
        Console.WriteLine(forwardHoldout ? "=== FROZEN FORWARD-TEST DETAILS ==="
            : "=== HISTORICAL REPLAY DETAILS ===");
        Console.WriteLine("Frozen training window: " + Day(plan.TrainingFrom) +
            " through " + Day(plan.TrainingThrough));
        Console.WriteLine((forwardHoldout ? "Forward SQL test window: " :
            "Historical SQL replay window: ") + Day(sessions[0].Day) +
            " through " + Day(sessions[^1].Day) +
            " (" + sessions.Count + " validated days)");
        var selectedPolicy = plan.SelectedPolicy ??
            new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions);
        Console.WriteLine("Portfolio policy: " + selectedPolicy.Description +
            (forwardHoldout ? " (chosen exclusively on 2020-2025 training)."
                : " (selected on MES_DATA training data only)."));
        Console.WriteLine(forwardHoldout
            ? "Frozen configurations and router were selected exclusively on dbo.MES_DATA_2020_2025."
            : "Frozen config IDs and rankings are calculated exclusively from MES_DATA.");

        if (forwardHoldout)
            Console.WriteLine("Forward exclusions use the checked-in 2025-2026 event calendar.");
        else
        {
            ValidateHistoricalReleaseCoverage(sessions[0].Day, sessions[^1].Day);
            Console.WriteLine("Historical event exclusions use the checked-in 2020-2025 release calendar.");
            Console.WriteLine("BLS, BEA, Census and Federal Reserve dates come from official archives;");
            Console.WriteLine("ISM first/third-business-day dates are rule-derived, with exceptions possible.");
            if (sessions[^1].Day >= CalendarStart)
                Console.WriteLine("Overlap note: dates from 2025-10-06 onward overlap the " +
                    "configuration-selection era; clean nonoverlap P&L is reported separately.");
        }

        var valid = sessions.Select(s => s.Day).ToHashSet();
        var orb = OptimizationData.Prepare(active.ToList(), all.ToList())
            .Where(s => s.Range is not null && valid.Contains(s.Day))
            .OrderBy(s => s.Day).ToArray();
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();

        var choices = new Dictionary<MultiStrategyKind, MultiStrategyRunner.Choice>();
        var individual = new Dictionary<MultiStrategyKind, List<MultiStrategyTrade>>();
        var selectedKinds = plan.Choices.Keys.OrderBy(k => k).ToArray();
        Console.WriteLine("Frozen adaptive configuration experts: " +
            (plan.AdaptiveExperts?.ExpertCount.ToString() ?? "none (baseline mode)"));
        Console.WriteLine("Replaying " + selectedKinds.Length +
            " frozen strategy families (no omitted family re-optimized).");
        foreach (var kind in selectedKinds)
        {
            var frozen = plan.Choices[kind];
            var generated = plan.AdaptiveExperts is null
                ? MultiStrategyEngine.Generate(frozen.Variant, sessions, orb)
                : new Dictionary<DateOnly, MultiStrategyTrade>();
            var trades = generated.Values.OrderBy(t => t.EntryAt).ToList();
            individual[kind] = trades;
            choices[kind] = new MultiStrategyRunner.Choice(
                frozen.Variant, frozen.Train, generated, frozen.Robustness,
                frozen.Early, frozen.Late);
            Console.WriteLine("Frozen " + kind + ": variant " + frozen.Variant.Id +
                ", original training net $" + F(frozen.Train.Net) +
                ", parameters " + frozen.Variant.Parameters);
        }

        if (plan.AdaptiveExperts is not null)
        {
            // Pass -1/-1: replay uses FROZEN training scores for router ranking.
            // Forward-test returns are never used to fit, disable or re-rank.
            choices = AdaptiveConfigurationEngine.Apply(
                plan.AdaptiveExperts, choices, sessions, orb, -1, -1);
            foreach (var kind in selectedKinds)
                individual[kind] = choices[kind].Trades.Values
                    .OrderBy(t => t.EntryAt).ToList();
            foreach (var (kind, states) in plan.AdaptiveExperts.Rules.OrderBy(x => x.Key))
                foreach (var (state, expert) in states.OrderBy(x => x.Key))
                    Console.WriteLine("REPLAY_EXPERT," + kind + "," + state +
                        ",config=" + expert.Variant.Id + ",mode=" +
                        expert.Variant.SignalMode + ",candle=" + expert.CandleRule);
        }

        // No evaluation-table P&L is used to choose strategy or routing priority.
        var original = MultiStrategyRunner.EvaluatePortfolio(
            selectedPolicy, choices, sessions, 0, sessions.Count);
        var fallback = MultiStrategyRunner.EvaluatePortfolio(
            selectedPolicy with { UseFallbacks = true }, choices,
            sessions, 0, sessions.Count);
        var allOos = original.Trades.OrderBy(t => t.EntryAt).ToArray();
        var regimeComparison = plan.RegimeComparisonPolicy is { } comparison
            ? MultiStrategyRunner.EvaluatePortfolio(comparison, choices,
                sessions, 0, sessions.Count)
            : null;

        if (plan.RegimeComparisonPolicy is { } comparisonPolicy)
        {
            Console.WriteLine();
            Console.WriteLine("=== FROZEN POINT-IN-TIME REGIME AUDIT ===");
            Console.WriteLine("REPLAY_REGIME_AUDIT,slot,regime,session_count");
            var thresholds = new RegimeThresholds(
                comparisonPolicy.RegimeTrendAtr,
                comparisonPolicy.RegimeMinEfficiency,
                comparisonPolicy.RegimeMaxVolatility,
                comparisonPolicy.RegimeMinLiquidity);
            foreach (var slot in Enum.GetValues<MultiStrategySlot>())
                foreach (var group in sessions.GroupBy(day =>
                    MarketRegimeEngine.Classify(
                        MarketRegimeEngine.ForSlot(day, slot), thresholds)))
                    Console.WriteLine("REPLAY_REGIME_AUDIT," + slot + "," +
                        group.Key + "," + group.Count());
        }

        Console.WriteLine();
        Console.WriteLine(forwardHoldout
            ? "=== FROZEN FORWARD-TEST ROUTING COMPARISON ==="
            : "=== FROZEN HISTORICAL ROUTING COMPARISON ===");
        Console.WriteLine("REPLAY_ROUTER,policy,net_usd,trades,win_rate_pct,max_drawdown_usd,fallback_fills");
        foreach (var outcome in new[] { original, fallback })
            Console.WriteLine("REPLAY_ROUTER," + outcome.Policy.Description + "," +
                F(outcome.Score.Net) + "," + outcome.Score.Trades + "," +
                F(outcome.Score.WinRate) + "," + F(outcome.Score.Drawdown) + "," +
                outcome.FallbackTrades);
        if (regimeComparison is not null)
            Console.WriteLine("REPLAY_REGIME_COMPARE," +
                regimeComparison.Policy.Description + "," +
                F(regimeComparison.Score.Net) + "," +
                regimeComparison.Score.Trades + "," +
                F(regimeComparison.Score.WinRate) + "," +
                F(regimeComparison.Score.Drawdown) + "," +
                regimeComparison.FallbackTrades);

        // In forward mode all evaluation trades are strictly later than training.
        Console.WriteLine();
        Console.WriteLine(forwardHoldout
            ? "=== FROZEN FORWARD RESULTS: " + selectedKinds.Length +
                " STRATEGIES + SELECTED PORTFOLIO ==="
            : "=== FROZEN REPLAY RESULTS: " + selectedKinds.Length +
                " STRATEGIES + SELECTED PORTFOLIO ===");
        Console.WriteLine("REPLAY_TOTAL,strategy,total_net_usd," +
            (forwardHoldout ? "nonoverlap_forward_net_usd" : "clean_pretraining_net_usd") +
            ",overlap_net_usd,trades,win_rate_pct,max_closed_trade_drawdown_usd");
        void Report(string name, IEnumerable<MultiStrategyTrade> source)
        {
            var trades = source.ToArray();
            var total = MultiScore.From(trades);
            var clean = MultiScore.From(trades.Where(t =>
                forwardHoldout ? t.Day > plan.TrainingThrough : t.Day < CalendarStart));
            var overlap = total.Net - clean.Net;
            Console.WriteLine("REPLAY_TOTAL," + name + "," + F(total.Net) + "," +
                F(clean.Net) + "," + F(overlap) + "," + total.Trades + "," +
                F(total.WinRate) + "," + F(total.Drawdown));
        }
        foreach (var kind in selectedKinds)
            Report(kind.ToString(), individual[kind]);
        Report("SelectedPortfolio", allOos);
        Report("FallbackPortfolio", fallback.Trades);
        if (regimeComparison is not null)
            Report("RegimeComparison", regimeComparison.Trades);

        Console.WriteLine();
        Console.WriteLine(forwardHoldout
            ? "=== FROZEN FORWARD-TEST YEAR-BY-YEAR PROFIT ==="
            : "=== FROZEN REPLAY YEAR-BY-YEAR PROFIT ===");
        Console.WriteLine("YEARLY_REPLAY,year,strategy,net_usd,trades,win_rate_pct");
        var yearlySources = individual.ToDictionary(x => x.Key.ToString(),
            x => (IEnumerable<MultiStrategyTrade>)x.Value);
        yearlySources["SelectedPortfolio"] = allOos;
        yearlySources["FallbackPortfolio"] = fallback.Trades;
        if (regimeComparison is not null)
            yearlySources["RegimeComparison"] = regimeComparison.Trades;
        var years = sessions.Select(x => x.Day.Year).Distinct().OrderBy(x => x).ToArray();
        foreach (var (name, allTrades) in yearlySources)
            foreach (var year in years)
            {
                var score = MultiScore.From(allTrades.Where(x => x.Day.Year == year));
                Console.WriteLine("YEARLY_REPLAY," + year + "," + name + "," +
                    F(score.Net) + "," + score.Trades + "," + F(score.WinRate));
            }

        var weekly = MultiStrategyRunner.BuildWeeklyOosRows(individual, allOos,
            sessions[0].Day, sessions[^1].Day);
        Console.WriteLine();
        Console.WriteLine(forwardHoldout
            ? "=== FROZEN FORWARD-TEST WEEKLY PROFIT (USD, Monday-Sunday ET) ==="
            : "=== FROZEN HISTORICAL WEEKLY PROFIT (USD, Monday-Sunday ET) ===");
        Console.WriteLine("WEEKLY_REPLAY,week_start_et,week_end_et,strategy,net_usd,cumulative_net_usd,trades");
        foreach (var row in weekly)
            Console.WriteLine("WEEKLY_REPLAY," + Day(row.WeekStart) + "," +
                Day(row.WeekEnd) + "," + row.Strategy + "," +
                F(row.Net) + "," + F(row.Cumulative) + "," + row.Trades);

        // Include fallback curve using the exact same weeks and schema.
        var fallbackMap = individual.ToDictionary(kv => kv.Key, kv => kv.Value);
        var fallbackWeekly = MultiStrategyRunner.BuildWeeklyOosRows(
            fallbackMap, fallback.Trades, sessions[0].Day, sessions[^1].Day)
            .Where(x => x.Strategy == "SelectedPortfolio");
        decimal last = 0m;
        foreach (var row in fallbackWeekly)
        {
            last = row.Cumulative;
            Console.WriteLine("WEEKLY_REPLAY," + Day(row.WeekStart) + "," +
                Day(row.WeekEnd) + ",FallbackPortfolio," +
                F(row.Net) + "," + F(row.Cumulative) + "," + row.Trades);
        }
        if (regimeComparison is not null)
        {
            var regimeWeeks = MultiStrategyRunner.BuildWeeklyOosRows(
                fallbackMap, regimeComparison.Trades,
                sessions[0].Day, sessions[^1].Day)
                .Where(x => x.Strategy == "SelectedPortfolio").ToArray();
            if (regimeWeeks.Sum(x => x.Net) != regimeComparison.Score.Net)
                throw new InvalidOperationException(
                    "Regime comparison weekly P&L does not reconcile.");
            foreach (var row in regimeWeeks)
                Console.WriteLine("WEEKLY_REPLAY," + Day(row.WeekStart) + "," +
                    Day(row.WeekEnd) + ",RegimeComparison," +
                    F(row.Net) + "," + F(row.Cumulative) + "," + row.Trades);
        }

        foreach (var (kind, trades) in individual)
            if (trades.Sum(t => t.Net) !=
                weekly.Where(w => w.Strategy == kind.ToString()).Sum(w => w.Net))
                throw new InvalidOperationException(
                    "Weekly replay reconciliation failure: " + kind);
        if (last != fallback.Score.Net ||
            weekly.Where(x => x.Strategy == "SelectedPortfolio").Sum(x => x.Net)
                != original.Score.Net)
            throw new InvalidOperationException("Historical portfolio weekly totals do not reconcile.");

        Console.WriteLine((forwardHoldout ? "Frozen forward evaluation" : "Historical replay") +
            " completed; no files written. Send terminal output to ChatGPT for charts.");
        return 0;
    }

    // Fail closed if the generated archive lacks a material class of major
    // announcements. For a full calendar year, a missing source must never
    // silently become "trade on every news day".
    internal static void ValidateHistoricalReleaseCoverage(
        DateOnly first, DateOnly last)
    {
        // Validate every historical calendar year touched by SQL, including
        // partial 2020 and 2025. The archive is fixed regardless of how many
        // SQL sessions happen to exist in a given calendar year.
        for (var year = Math.Max(2020, first.Year);
             year <= Math.Min(2025, last.Year); year++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var day = new DateOnly(year, 1, 1);
                 day.Year == year; day = day.AddDays(1))
                foreach (var item in EconomicCalendar.Events(day))
                    counts[item.Code] = counts.GetValueOrDefault(item.Code) + 1;
            var minimums = new Dictionary<string, int> {
                ["CPI"] = 11, ["NFP"] = 11, ["PPI"] = 11,
                ["JOLTS"] = 11, ["PCE"] = 10,
                ["GDP"] = 10, ["RETAIL_SALES"] = 10,
                ["ISM_MANUFACTURING"] = 11, ["ISM_SERVICES"] = 11,
                ["FOMC_DECISION"] = 7, ["FOMC_MINUTES"] = 7
            };
            if (year == 2020)
            {
                // The input SQL begins in September 2020. The archived PCE
                // file is sparse before then; the 2020 FOMC archive is absent
                // from the current Fed page, so relevant releases are pinned.
                minimums["PCE"] = 8;
                minimums["FOMC_DECISION"] = 3;
                minimums["FOMC_MINUTES"] = 2;
            }
            if (year == 2025)
            {
                // Generated archive ends on October 5, before the 2025
                // shutdown reschedules. Rate decisions through September: 6.
                foreach (var code in minimums.Keys.ToArray())
                    minimums[code] = 8;
                minimums["FOMC_DECISION"] = 6;
                minimums["FOMC_MINUTES"] = 6;
            }
            foreach (var (code, min) in minimums)
                if (counts.GetValueOrDefault(code) < min)
                    throw new InvalidOperationException(
                        $"Historical economic calendar incomplete for {year} " +
                        $"({code}: {counts.GetValueOrDefault(code)}, need at least {min}). " +
                        "Do not rely on the replay until the archived dates are complete.");
        }
    }

    private static string F(decimal amount) => amount.ToString("0.00", Ci);
    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", Ci);
}
