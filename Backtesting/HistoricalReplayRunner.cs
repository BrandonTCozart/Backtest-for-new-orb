using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Replays FROZEN last-fold MES_DATA walk-forward variants against the older
/// MES_DATA_2020_2025 table. No strategy search or historical re-ranking.
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
        IReadOnlyDictionary<MultiStrategyKind, FrozenChoice> Choices);

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
        FrozenPlan plan, IReadOnlyList<PriceBar> active, IReadOnlyList<PriceBar> all)
    {
        var sessions = MultiStrategyData.Build(active, all, "unused");
        if (sessions.Count == 0)
        {
            Console.Error.WriteLine(
                "No complete 390-minute RTH sessions found in MES_DATA_2020_2025.");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("=== HISTORICAL REPLAY DETAILS ===");
        Console.WriteLine("Frozen training window: " + Day(plan.TrainingFrom) +
            " through " + Day(plan.TrainingThrough));
        Console.WriteLine("Historical SQL replay window: " + Day(sessions[0].Day) +
            " through " + Day(sessions[^1].Day) +
            " (" + sessions.Count + " validated days)");
        Console.WriteLine("Portfolio policy: Fixed_best_per_session (no fallback), " +
            "exactly as selected in the latest MES_DATA fold.");
        Console.WriteLine("Frozen config IDs and rankings are calculated exclusively from MES_DATA.");

        ValidateHistoricalReleaseCoverage(sessions[0].Day, sessions[^1].Day);
        Console.WriteLine("Historical event exclusions use the checked-in 2020-2025 release calendar.");
        Console.WriteLine("BLS, BEA, Census and Federal Reserve dates come from official archives;");
        Console.WriteLine("ISM first/third-business-day dates are rule-derived, with exceptions possible.");
        if (sessions[^1].Day >= CalendarStart)
            Console.WriteLine("Overlap note: dates from 2025-10-06 onward overlap the " +
                              "configuration-selection era; clean nonoverlap P&L is reported separately.");

        var valid = sessions.Select(s => s.Day).ToHashSet();
        var orb = OptimizationData.Prepare(active.ToList(), all.ToList())
            .Where(s => s.Range is not null && valid.Contains(s.Day))
            .OrderBy(s => s.Day).ToArray();
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();

        var choices = new Dictionary<MultiStrategyKind, MultiStrategyRunner.Choice>();
        var individual = new Dictionary<MultiStrategyKind, List<MultiStrategyTrade>>();
        foreach (var kind in Enum.GetValues<MultiStrategyKind>())
        {
            var frozen = plan.Choices[kind];
            var generated = MultiStrategyEngine.Generate(frozen.Variant, sessions, orb);
            var trades = generated.Values.OrderBy(t => t.EntryAt).ToList();
            individual[kind] = trades;
            choices[kind] = new MultiStrategyRunner.Choice(
                frozen.Variant, frozen.Train, generated, frozen.Robustness,
                frozen.Early, frozen.Late);
            Console.WriteLine("Frozen " + kind + ": variant " + frozen.Variant.Id +
                ", original training net $" + F(frozen.Train.Net) +
                ", parameters " + frozen.Variant.Parameters);
        }

        // No older-table P&L is used to choose the strategy or priority.
        var basePolicy = new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions);
        var original = MultiStrategyRunner.EvaluatePortfolio(
            basePolicy, choices, sessions, 0, sessions.Count);
        var fallback = MultiStrategyRunner.EvaluatePortfolio(
            basePolicy with { UseFallbacks = true }, choices, sessions, 0, sessions.Count);
        var allOos = original.Trades.OrderBy(t => t.EntryAt).ToArray();

        Console.WriteLine();
        Console.WriteLine("=== FROZEN HISTORICAL ROUTING COMPARISON ===");
        Console.WriteLine("REPLAY_ROUTER,policy,net_usd,trades,win_rate_pct,max_drawdown_usd,fallback_fills");
        foreach (var outcome in new[] { original, fallback })
            Console.WriteLine("REPLAY_ROUTER," + outcome.Policy.Description + "," +
                F(outcome.Score.Net) + "," + outcome.Score.Trades + "," +
                F(outcome.Score.WinRate) + "," + F(outcome.Score.Drawdown) + "," +
                outcome.FallbackTrades);

        // The selection and 2020–2025 P&L are independent except for overlap
        // dates from Oct 2025 onward, which are broken out explicitly.
        Console.WriteLine();
        Console.WriteLine("=== FROZEN REPLAY RESULTS: 19 STRATEGIES + SELECTED PORTFOLIO ===");
        Console.WriteLine("REPLAY_TOTAL,strategy,total_net_usd,clean_pretraining_net_usd,overlap_net_usd,trades,win_rate_pct,max_closed_trade_drawdown_usd");
        void Report(string name, IEnumerable<MultiStrategyTrade> source)
        {
            var trades = source.ToArray();
            var total = MultiScore.From(trades);
            var clean = MultiScore.From(trades.Where(t => t.Day < CalendarStart));
            var overlap = total.Net - clean.Net;
            Console.WriteLine("REPLAY_TOTAL," + name + "," + F(total.Net) + "," +
                F(clean.Net) + "," + F(overlap) + "," + total.Trades + "," +
                F(total.WinRate) + "," + F(total.Drawdown));
        }
        foreach (var kind in Enum.GetValues<MultiStrategyKind>())
            Report(kind.ToString(), individual[kind]);
        Report("SelectedPortfolio", allOos);
        Report("FallbackPortfolio", fallback.Trades);

        Console.WriteLine();
        Console.WriteLine("=== FROZEN REPLAY YEAR-BY-YEAR PROFIT ===");
        Console.WriteLine("YEARLY_REPLAY,year,strategy,net_usd,trades,win_rate_pct");
        var yearlySources = individual.ToDictionary(x => x.Key.ToString(),
            x => (IEnumerable<MultiStrategyTrade>)x.Value);
        yearlySources["SelectedPortfolio"] = allOos;
        yearlySources["FallbackPortfolio"] = fallback.Trades;
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
        Console.WriteLine("=== FROZEN HISTORICAL WEEKLY PROFIT (USD, Monday-Sunday ET) ===");
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

        foreach (var (kind, trades) in individual)
            if (trades.Sum(t => t.Net) !=
                weekly.Where(w => w.Strategy == kind.ToString()).Sum(w => w.Net))
                throw new InvalidOperationException(
                    "Weekly replay reconciliation failure: " + kind);
        if (last != fallback.Score.Net ||
            weekly.Where(x => x.Strategy == "SelectedPortfolio").Sum(x => x.Net)
                != original.Score.Net)
            throw new InvalidOperationException("Historical portfolio weekly totals do not reconcile.");

        Console.WriteLine("Historical replay completed; no files written. " +
                          "Send this terminal output to ChatGPT for charts.");
        return 0;
    }

    // Fail closed if the generated archive lacks a material class of major
    // announcements. For a full calendar year, a missing source must never
    // silently become "trade on every news day".
    internal static void ValidateHistoricalReleaseCoverage(
        DateOnly first, DateOnly last)
    {
        for (var year = first.Year; year <= last.Year; year++)
        {
            if (new DateOnly(year, 1, 1) < first ||
                new DateOnly(year, 12, 31) > last)
                continue;
            if (year < 2020 || year > 2025) continue;
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
                ["FOMC_DECISION"] = 7
            };
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
