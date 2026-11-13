using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

// Separate, opt-in ORB opening-window discovery experiment. The existing
// ORB, five-strategy pipeline, variant IDs and historical replay are untouched.
// Fit 09:30-09:44 shapes on TRAIN; select ORB configs on TRAIN; validate/disable
// on a disjoint VALIDATION block; report the final TEST block only afterward.
public static class OrbOpeningPatternExperiment
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const int OpeningBars = 15;
    private static readonly int[] PathEnds = [2, 5, 8, 11, 14];

    internal sealed record OpeningProfile(
        DateOnly Day, double[] Features, decimal Width, decimal Move,
        double SignedVolumeProxy, double FirstMinuteVolumeShare,
        double Efficiency, double RelativeVolume, bool HasPriorReference);

    internal sealed record Split(int TrainEnd, int ValidationEnd, int Count)
    {
        public int TrainCount => TrainEnd;
        public int ValidationCount => ValidationEnd - TrainEnd;
        public int TestCount => Count - ValidationEnd;
    }

    internal sealed record Selected(MultiStrategyVariant Variant,
        MultiScore Training, decimal TrainingRank);

    internal sealed record ExperimentResult(
        Split Split, OpeningClusterModel Clusters,
        IReadOnlyDictionary<int, Selected> Specialists,
        IReadOnlyDictionary<int, bool> Enabled,
        Selected? Global,
        PeriodTrades Validation,
        PeriodTrades Test,
        IReadOnlyList<MultiStrategyTrade> TestValidated);

    public static async Task<int> RunAsync(
        string? connectionString, string symbol, string timeZone,
        MultiSearchProfile profile, int groupCount = 4)
    {
        Console.WriteLine("=== OPENING-WINDOW ORB GROUP RESEARCH (NO LIVE ORDERS) ===");
        Console.WriteLine("This separate mode does not change the active multi-strategy bot.");
        Console.WriteLine("Reading historical ES futures from dbo.MES_DATA_2020_2025.");
        var progress = new OrbResearchProgress();
        // SQL loading has no row-count callback; its duration is unknown.
        progress.Start(1, "Loading SQL history (indeterminate)", 1);
        var market = await SqlMarketDataLoader.LoadAsync(connectionString,
            symbol, timeZone, tableName: "MES_DATA_2020_2025");
        progress.Complete();
        return RunCore(market.ActiveBars, market.AllContractBars,
            profile, groupCount, progress);
    }

    public static int Run(IReadOnlyList<PriceBar> active,
        IReadOnlyList<PriceBar> all, MultiSearchProfile profile,
        int groupCount = 4)
    {
        var progress = new OrbResearchProgress();
        progress.Start(1, "Using supplied ES candles", 1);
        progress.Complete();
        return RunCore(active, all, profile, groupCount, progress);
    }

    private static int RunCore(IReadOnlyList<PriceBar> active,
        IReadOnlyList<PriceBar> all, MultiSearchProfile profile,
        int groupCount, OrbResearchProgress progress)
    {
        if (groupCount is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(groupCount),
                "ORB pattern groups must be between 2 and 8.");
        progress.Start(2, "Prepare complete trading sessions", 3);
        var complete = MultiStrategyData.Build(active, all, "unused");
        progress.Advance();
        if (complete.Count > 0 && complete[0].Day.Year <= 2025)
            HistoricalReplayRunner.ValidateHistoricalReleaseCoverage(
                complete[0].Day, complete[^1].Day);
        var valid = complete.Select(x => x.Day).ToHashSet();
        var usableOrb = OptimizationData.Prepare(active.ToList(), all.ToList())
            .Where(x => x.Range is not null && valid.Contains(x.Day))
            .ToDictionary(x => x.Day);
        progress.Advance();
        // Index by EXACT date rather than relying on two independent session
        // builders to return the same count or implicit array alignment.
        var sessions = complete.Where(x => usableOrb.ContainsKey(x.Day)).ToArray();
        var orb = sessions.Select(x => usableOrb[x.Day]).ToArray();
        progress.Complete();
        Console.WriteLine("ORB pattern eligible full-length RTH days: " +
            sessions.Length + "; missing usable ORB ranges: " +
            (complete.Count - sessions.Length));
        if (sessions.Length < 25)
        {
            Console.Error.WriteLine("ORB group research needs at least 25 valid ORB opening windows.");
            return 2;
        }
        var run = Research(sessions, orb, profile, groupCount, progress);
        progress.Start(7, "Produce summary and weekly results", 1);
        Report(run, sessions);
        progress.Complete();
        Console.WriteLine("ORB opening-window research complete; no files written and no SQL modified.");
        return 0;
    }

    internal static Split MakeSplit(int n)
    {
        if (n < 25) throw new ArgumentOutOfRangeException(nameof(n));
        var train = n * 3 / 5;
        var validation = n * 4 / 5;
        return new Split(train, validation, n);
    }

    // Every feature is computed from the 15 *COMPLETED* 09:30-09:44 candles.
    // First-period up/down candle volume is only a DIRECTIONAL PROXY; OHLCV
    // cannot observe aggressive buyer/seller initiated volume or order flow.
    internal static OpeningProfile Extract(MultiSession session,
        MultiSession? previous)
    {
        var bars = session.Rth;
        if (bars.Length < OpeningBars)
            throw new ArgumentException("Incomplete ORB opening window.");
        var firstOpen = bars[0].C.O;
        var high = bars.Take(OpeningBars).Max(b => b.C.H);
        var low = bars.Take(OpeningBars).Min(b => b.C.L);
        var width = Math.Max(0.25m, high - low);
        var lastClose = bars[14].C.C;
        decimal totalVolume = 0m, signedVolume = 0m,
            travel = 0m, lowerWicks = 0m, upperWicks = 0m;
        var previousDirection = 0;
        var turns = 0;
        for (var i = 0; i < OpeningBars; i++)
        {
            var c = bars[i].C;
            var v = Math.Max(0L, c.V);
            totalVolume += v;
            signedVolume += c.C > c.O ? v : c.C < c.O ? -v : 0m;
            lowerWicks += Math.Max(0m, Math.Min(c.C, c.O) - c.L);
            upperWicks += Math.Max(0m, c.H - Math.Max(c.C, c.O));
            var priorClose = i == 0 ? c.O : bars[i - 1].C.C;
            var diff = c.C - priorClose;
            travel += Math.Abs(diff);
            var direction = Math.Sign(diff);
            if (direction != 0)
            {
                if (previousDirection != 0 && direction != previousDirection)
                    turns++;
                previousDirection = direction;
            }
        }
        var first5 = bars.Take(5).Sum(x => (decimal)Math.Max(0L, x.C.V));
        var last5 = bars.Skip(10).Take(5)
            .Sum(x => (decimal)Math.Max(0L, x.C.V));
        var hasPrior = previous is not null &&
            session.PriorCompleteRthDate == previous.Day &&
            string.Equals(session.Contract, previous.Contract,
                StringComparison.OrdinalIgnoreCase);
        var previousVolume = hasPrior
            ? previous!.Rth.Take(OpeningBars)
                .Sum(x => (decimal)Math.Max(0L, x.C.V)) : 0m;
        var relativeVolume = hasPrior && previousVolume > 0m
            ? Clamp((double)(totalVolume / previousVolume), 0, 5) : 1d;
        var net = lastClose - firstOpen;
        var efficiency = travel > 0m
            ? Clamp((double)(Math.Abs(net) / travel), 0, 1) : 0d;
        var firstMinuteShare = totalVolume > 0m
            ? Clamp((double)(bars[0].C.V / totalVolume), 0, 1) : 0d;
        var signedProxy = totalVolume > 0m
            ? Clamp((double)(signedVolume / totalVolume), -1, 1) : 0d;
        var features = new List<double> {
            Clamp((double)(session.OpeningWidth /
                Math.Max(0.25m, session.EarlyAtr)), 0, 10),
            Clamp((double)(net / width), -2, 2),
            Clamp((double)((lastClose - low) / width), 0, 1),
            Clamp((double)((bars[0].C.C - bars[0].C.O) /
                Math.Max(0.25m, bars[0].C.H - bars[0].C.L)), -1, 1),
            Clamp(firstMinuteShare * 15d, 0, 8),
            signedProxy,
            Clamp((double)((last5 + 1m) / (first5 + 1m)), 0, 5),
            efficiency,
            (double)turns / 14d,
            Clamp((double)((lowerWicks - upperWicks) /
                Math.Max(0.25m, lowerWicks + upperWicks)), -1, 1),
            Math.Log(relativeVolume + 0.01),
            hasPrior ? 1d : 0d
        };
        foreach (var i in PathEnds)
            features.Add(Clamp((double)((bars[i].C.C - firstOpen) / width), -2, 2));
        return new OpeningProfile(session.Day, features.ToArray(),
            session.OpeningWidth, net, signedProxy, firstMinuteShare,
            efficiency, relativeVolume, hasPrior);
    }

    internal static OpeningProfile[] ExtractProfiles(IReadOnlyList<MultiSession> sessions)
    {
        var indexed = sessions.ToDictionary(s => s.Day);
        return sessions.Select(s => Extract(s,
            s.PriorCompleteRthDate is { } previous &&
            indexed.TryGetValue(previous, out var prior) ? prior : null)).ToArray();
    }

    internal static ExperimentResult Research(
        IReadOnlyList<MultiSession> sessions,
        IReadOnlyList<SessionData> orb,
        MultiSearchProfile profile, int groupCount,
        OrbResearchProgress? progress = null)
    {
        progress ??= new OrbResearchProgress();
        var split = MakeSplit(sessions.Count);
        progress.Start(3, "Discover opening-window groups", 3);
        var profiles = ExtractProfiles(sessions);
        progress.Advance();
        var model = OpeningClusterModel.Fit(
            profiles.Take(split.TrainEnd).ToArray(), groupCount);
        progress.Advance();
        var labels = profiles.Select(model.Assign).ToArray();
        progress.Complete();
        var trainDays = sessions.Take(split.TrainEnd).ToArray();
        var trainOrb = orb.Take(split.TrainEnd).ToArray();
        var candidates = MultiStrategyEngine.EnumerateVariants(profile)
            .Where(v => v.Kind == MultiStrategyKind.Orb).ToArray();

        var trainGroupCounts = Enumerable.Range(0, groupCount)
            .ToDictionary(g => g, g => labels.Take(split.TrainEnd).Count(x => x == g));
        var global = (Selected?)null;
        var specialist = new Dictionary<int, Selected>();
        MultiStrategyEngine.ClearCaches();
        OptimizationEngine.ClearCaches();
        var evaluations = 0;

        Console.WriteLine("ORB_PATTERN_SPLIT,train=" + split.TrainCount +
            ",validation=" + split.ValidationCount + ",test=" + split.TestCount +
            ",train_start=" + Date(sessions[0].Day) +
            ",train_end=" + Date(sessions[split.TrainEnd - 1].Day) +
            ",validation_start=" + Date(sessions[split.TrainEnd].Day) +
            ",validation_end=" + Date(sessions[split.ValidationEnd - 1].Day) +
            ",test_start=" + Date(sessions[split.ValidationEnd].Day) +
            ",test_end=" + Date(sessions[^1].Day));
        Console.WriteLine("ORB_PATTERN_SEARCH,profile=" + profile +
            ",groups=" + groupCount + ",base_configs=" + candidates.Length +
            ",direction_evaluations=" + (candidates.Length * 2));
        Console.WriteLine("Volume by candle direction is a proxy, not actual aggressor volume.");
        progress.Start(4, "Optimize ORB configurations (training only)",
            candidates.Length * 2);

        // CRUCIAL: no later candles are ever simulated while choosing experts.
        // All evaluation dates here belong to TRAIN only.
        foreach (var candidate in candidates)
        {
            foreach (var side in new[] { SignalMode.Original, SignalMode.Reversed })
            {
                var v = candidate with { SignalMode = side };
                var produced = MultiStrategyEngine.Generate(v, trainDays, trainOrb);
                var ordered = produced.Values.OrderBy(t => t.Day).ToArray();
                if (TryRank(ordered, split.TrainCount,
                    minTrades: Math.Max(12, split.TrainCount / 10),
                    chronologicalHalfSplit: sessions[split.TrainEnd / 2].Day,
                    out var overallScore, out var overallRank) &&
                    Better(overallRank, overallScore, v, global))
                    global = new Selected(v, overallScore, overallRank);

                // Each group gets its own best settings and can be disabled
                // if no configuration has enough training support.
                foreach (var group in trainGroupCounts.Keys)
                {
                    var grouped = ordered.Where(t =>
                        labels[IndexOfDay(trainDays, t.Day)] == group).ToArray();
                    if (!TryRank(grouped, trainGroupCounts[group],
                        minTrades: Math.Max(12, trainGroupCounts[group] / 10),
                        chronologicalHalfSplit: sessions[split.TrainEnd / 2].Day,
                        out var score, out var rank))
                        continue;
                    specialist.TryGetValue(group, out var old);
                    if (Better(rank, score, v, old))
                        specialist[group] = new Selected(v, score, rank);
                }
                evaluations++;
                progress.Update(evaluations);
            }
            // The original ORB entry/trade caches otherwise grow for tens of
            // thousands of 1-minute ES candidate simulations.
            if (evaluations % 96 == 0)
                OptimizationEngine.ClearCaches();
            // Progress bar prints every 5% (rather than every raw evaluation).
        }

        // Freeze all selected IDs before examining validation P&L.
        OptimizationEngine.ClearCaches();
        progress.Start(5, "Replay validation and select tradeable groups",
            PeriodProgressSteps(global, specialist, groupCount) + groupCount);
        var validation = SimulatePeriod(sessions, orb,
            split.TrainEnd, split.ValidationEnd,
            global, specialist, labels, groupCount, progress);
        var enabled = new Dictionary<int, bool>();
        for (var group = 0; group < groupCount; group++)
        {
            var groupValid = validation.ByGroup.GetValueOrDefault(group, []);
            var count = labels.Skip(split.TrainEnd)
                .Take(split.ValidationCount).Count(x => x == group);
            var score = MultiScore.From(groupValid);
            var baselineForGroup = MultiScore.From(validation.Global.Where(t =>
                labels[IndexOfDay(sessions, t.Day)] == group));
            enabled[group] = specialist.ContainsKey(group) &&
                count >= 15 && score.Trades >= Math.Max(4, count / 15) &&
                score.Net > score.Trades * 10m &&
                score.Net - 10m * score.Trades >
                    baselineForGroup.Net - 10m * baselineForGroup.Trades &&
                score.Drawdown < Math.Max(2000m, score.Net * 2m);
            progress.Advance();
        }

        // After validation-based enable/disable choices are fixed, use the
        // final chronological TEST block exactly once.
        OptimizationEngine.ClearCaches();
        progress.Start(6, "Replay final untouched-by-fit test block",
            PeriodProgressSteps(global, specialist, groupCount));
        var test = SimulatePeriod(sessions, orb,
            split.ValidationEnd, split.Count,
            global, specialist, labels, groupCount, progress);
        var accepted = test.ByGroup
            .Where(x => enabled.GetValueOrDefault(x.Key))
            .SelectMany(x => x.Value).OrderBy(t => t.EntryAt).ToArray();
        return new ExperimentResult(split, model, specialist, enabled, global,
            validation, test, accepted);
    }

    private static int IndexOfDay(
        IReadOnlyList<MultiSession> sessions, DateOnly day)
    {
        // Training data are sorted by session date. Binary search avoids a
        // quadratic lookup in the candidate grid.
        var low = 0;
        var high = sessions.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var compare = sessions[mid].Day.CompareTo(day);
            if (compare == 0) return mid;
            if (compare < 0) low = mid + 1;
            else high = mid - 1;
        }
        throw new InvalidOperationException("ORB signal date missing from validated calendar.");
    }

    internal sealed record PeriodTrades(
        IReadOnlyList<MultiStrategyTrade> Global,
        IReadOnlyList<MultiStrategyTrade> AllSpecialists,
        IReadOnlyDictionary<int, IReadOnlyList<MultiStrategyTrade>> ByGroup);

    private static int PeriodProgressSteps(Selected? global,
        IReadOnlyDictionary<int, Selected> specialists, int groupCount) =>
        specialists.Values.Select(x => x.Variant)
            .Concat(global is null ? [] : [global.Variant])
            .DistinctBy(v => (v.Id, v.SignalMode))
            .Count() + groupCount;

    private static PeriodTrades SimulatePeriod(
        IReadOnlyList<MultiSession> sessions, IReadOnlyList<SessionData> orb,
        int start, int end, Selected? global,
        IReadOnlyDictionary<int, Selected> specialists, int[] labels, int count,
        OrbResearchProgress? progress = null)
    {
        var days = sessions.Skip(start).Take(end - start).ToArray();
        var orbDays = orb.Skip(start).Take(end - start).ToArray();
        var unique = specialists.Values.Select(x => x.Variant)
            .Concat(global is null ? [] : [global.Variant])
            .DistinctBy(v => (v.Id, v.SignalMode)).ToArray();
        var generated = new Dictionary<(int, SignalMode),
            IReadOnlyDictionary<DateOnly, MultiStrategyTrade>>();
        foreach (var variant in unique)
        {
            generated[(variant.Id, variant.SignalMode)] =
                MultiStrategyEngine.Generate(variant, days, orbDays);
            progress?.Advance();
        }
        IReadOnlyList<MultiStrategyTrade> GlobalTrades() =>
            global is null ? [] :
            generated[(global.Variant.Id, global.Variant.SignalMode)].Values
                .OrderBy(t => t.EntryAt).ToArray();
        var groupTrades = new Dictionary<int, IReadOnlyList<MultiStrategyTrade>>();
        for (var g = 0; g < count; g++)
        {
            if (!specialists.TryGetValue(g, out var chosen))
            {
                groupTrades[g] = [];
                progress?.Advance();
                continue;
            }
            var possible = generated[(chosen.Variant.Id, chosen.Variant.SignalMode)];
            var selected = new List<MultiStrategyTrade>();
            for (var i = start; i < end; i++)
                if (labels[i] == g && possible.TryGetValue(sessions[i].Day, out var trade))
                    selected.Add(trade);
            groupTrades[g] = selected;
            progress?.Advance();
        }
        return new PeriodTrades(GlobalTrades(),
            groupTrades.Values.SelectMany(x => x)
                .OrderBy(t => t.EntryAt).ToArray(), groupTrades);
    }

    // Trades are already net of $10 estimated ES execution costs. Require
    // profitability with an additional $10/trade to stress $20 total costs.
    private static bool TryRank(IReadOnlyList<MultiStrategyTrade> trades,
        int groupDays, int minTrades, DateOnly chronologicalHalfSplit,
        out MultiScore result, out decimal ranking)
    {
        result = MultiScore.From(trades);
        ranking = 0m;
        if (groupDays < 20 || result.Trades < minTrades ||
            result.Net <= result.Trades * 10m)
            return false;
        // Half split is determined ONLY by training dates, never trade outcomes.
        var splitDay = chronologicalHalfSplit;
        // caller supplies chronological split on both global and groups
        var left = MultiScore.From(trades.Where(x => x.Day < splitDay));
        var right = MultiScore.From(trades.Where(x => x.Day >= splitDay));
        if (left.Trades < 4 || right.Trades < 4 ||
            left.Net <= 0m || right.Net <= 0m)
            return false;
        ranking = result.Net - 10m * result.Trades -
            0.5m * result.Drawdown -
            0.5m * (Math.Max(0m, -left.Net) + Math.Max(0m, -right.Net));
        return ranking > 0m;
    }

    private static bool Better(decimal rank, MultiScore score,
        MultiStrategyVariant variant, Selected? selected) =>
        selected is null || rank > selected.TrainingRank ||
        rank == selected.TrainingRank &&
        (score.Net > selected.Training.Net ||
         score.Net == selected.Training.Net &&
         (variant.Id < selected.Variant.Id ||
          variant.Id == selected.Variant.Id &&
          variant.SignalMode < selected.Variant.SignalMode));

    private static void Report(ExperimentResult run,
        IReadOnlyList<MultiSession> sessions)
    {
        var split = run.Split;
        var profiles = ExtractProfiles(sessions);
        var labels = profiles.Select(run.Clusters.Assign).ToArray();
        Console.WriteLine("ORB_PATTERN_FEATURES,width_over_atr,net_direction,close_location," +
            "first_candle_body,first_candle_volume_share,signed_candle_volume_proxy," +
            "volume_acceleration,efficiency,direction_reversals,wick_imbalance," +
            "relative_volume_vs_prior_complete_session,prior_available," +
            "five_normalized_intrawindow_path_checkpoints");
        Console.WriteLine("ORB_PATTERN_GROUP,group,train_days,validation_days,test_days," +
            "avg_range_points,avg_net_move_points,avg_bullish_volume_proxy," +
            "avg_first_candle_volume_share,avg_efficiency");
        for (var group = 0; group < run.Clusters.GroupCount; group++)
        {
            var training = profiles.Take(split.TrainEnd)
                .Where(p => run.Clusters.Assign(p) == group).ToArray();
            var validation = labels.Skip(split.TrainEnd)
                .Take(split.ValidationCount).Count(x => x == group);
            var test = labels.Skip(split.ValidationEnd).Count(x => x == group);
            Console.WriteLine("ORB_PATTERN_GROUP," + group + "," +
                training.Length + "," + validation + "," + test + "," +
                F(training.Length == 0 ? 0m : training.Average(x => x.Width)) + "," +
                F(training.Length == 0 ? 0m : training.Average(x => x.Move)) + "," +
                F(training.Length == 0 ? 0 : training.Average(x => x.SignedVolumeProxy)) + "," +
                F(training.Length == 0 ? 0 : training.Average(x => x.FirstMinuteVolumeShare)) + "," +
                F(training.Length == 0 ? 0 : training.Average(x => x.Efficiency)));
        }

        Console.WriteLine("ORB_PATTERN_SELECTED,group,config,signal_mode,train_net," +
            "train_trades,train_drawdown,train_rank,validation_net," +
            "validation_trades,validation_drawdown,validation_global_net," +
            "validation_gate,settings");
        foreach (var group in Enumerable.Range(0, run.Clusters.GroupCount))
        {
            if (!run.Specialists.TryGetValue(group, out var choice))
            {
                Console.WriteLine("ORB_PATTERN_SELECTED," + group +
                    ",none,none,0,0,0,0,0,0,0,0,OFF,insufficient_training_evidence");
                continue;
            }
            var valScore = MultiScore.From(
                run.Validation.ByGroup.GetValueOrDefault(group, []));
            var baselineGroup = MultiScore.From(
                run.Validation.Global.Where(t =>
                    labels[IndexOfDay(sessions, t.Day)] == group));
            Console.WriteLine("ORB_PATTERN_SELECTED," + group + "," +
                choice.Variant.Id + "," + choice.Variant.SignalMode + "," +
                F(choice.Training.Net) + "," + choice.Training.Trades + "," +
                F(choice.Training.Drawdown) + "," + F(choice.TrainingRank) +
                "," + F(valScore.Net) + "," + valScore.Trades +
                "," + F(valScore.Drawdown) + "," + F(baselineGroup.Net) +
                "," + (run.Enabled.GetValueOrDefault(group) ? "ON" : "OFF") +
                "," + choice.Variant.Parameters);
        }
        if (run.Global is { } baseline)
            Console.WriteLine("ORB_PATTERN_GLOBAL,config=" + baseline.Variant.Id +
                ",direction=" + baseline.Variant.SignalMode +
                ",train_net=" + F(baseline.Training.Net) +
                ",train_trades=" + baseline.Training.Trades +
                ",settings=" + baseline.Variant.Parameters);
        else
            Console.WriteLine("ORB_PATTERN_GLOBAL,no eligible baseline; no-trade control");

        Console.WriteLine("ORB_PATTERN_TOTAL,period,model,net_usd,trades," +
            "win_rate_pct,max_drawdown_usd,net_if_20_cost,net_if_30_cost");
        void Total(string period, string model,
            IReadOnlyList<MultiStrategyTrade> trades)
        {
            var r = MultiScore.From(trades);
            Console.WriteLine("ORB_PATTERN_TOTAL," + period + "," + model + "," +
                F(r.Net) + "," + r.Trades + "," + F(r.WinRate) + "," +
                F(r.Drawdown) + "," + F(r.Net - 10m * r.Trades) + "," +
                F(r.Net - 20m * r.Trades));
        }
        Total("VALIDATION", "Global_ORB", run.Validation.Global);
        Total("VALIDATION", "Grouped_ORB_no_gate", run.Validation.AllSpecialists);
        var validatedDuringValidation = run.Validation.ByGroup
            .Where(x => run.Enabled.GetValueOrDefault(x.Key))
            .SelectMany(x => x.Value).ToArray();
        Total("VALIDATION", "Grouped_ORB_validation_gated",
            validatedDuringValidation);
        Total("TEST", "Global_ORB", run.Test.Global);
        Total("TEST", "Grouped_ORB_no_gate", run.Test.AllSpecialists);
        Total("TEST", "Grouped_ORB_validation_gated", run.TestValidated);
        Console.WriteLine("ORB_PATTERN_GROUP_TEST,group,net_usd,trades,win_rate_pct,max_drawdown_usd,validation_gate");
        for (var g = 0; g < run.Clusters.GroupCount; g++)
        {
            var score = MultiScore.From(run.Test.ByGroup.GetValueOrDefault(g, []));
            Console.WriteLine("ORB_PATTERN_GROUP_TEST," + g + "," +
                F(score.Net) + "," + score.Trades + "," +
                F(score.WinRate) + "," + F(score.Drawdown) + "," +
                (run.Enabled.GetValueOrDefault(g) ? "ON" : "OFF"));
        }
        Console.WriteLine("ORB_PATTERN_WEEKLY,week_start_et,model,net_usd,cumulative_net_usd,trades");
        var testFrom = sessions[split.ValidationEnd].Day;
        var testTo = sessions[^1].Day;
        foreach (var (name, trades) in new[] {
            ("Global_ORB", run.Test.Global),
            ("Grouped_ORB_no_gate", run.Test.AllSpecialists),
            ("Grouped_ORB_validation_gated", run.TestValidated)
        })
        {
            var rows = MultiStrategyRunner.BuildWeeklyOosRows(
                new Dictionary<MultiStrategyKind, List<MultiStrategyTrade>>(),
                trades.ToList(), testFrom, testTo)
                .Where(x => x.Strategy == "SelectedPortfolio");
            foreach (var week in rows)
                Console.WriteLine("ORB_PATTERN_WEEKLY," + Date(week.WeekStart) +
                    "," + name + "," + F(week.Net) + "," +
                    F(week.Cumulative) + "," + week.Trades);
        }
        Console.WriteLine("Research caution: opening-window clusters and parameters " +
            "were fit on historical data already used in earlier research; " +
            "the final block is CHRONOLOGICALLY unseen by this fit, not " +
            "a pristine never-inspected market period.");
    }

    private static double Clamp(double x, double min, double max) =>
        Math.Clamp(double.IsFinite(x) ? x : 0d, min, max);
    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", Ci);
    private static string F(decimal v) => v.ToString("0.00", Ci);
    private static string F(double v) => v.ToString("0.000", Ci);
}

// Deterministic, label-free k-means. BOTH scaling statistics and centroids
// come ONLY from the first chronological training block.
internal sealed class OpeningClusterModel
{
    private readonly double[] _means;
    private readonly double[] _stds;
    private readonly double[][] _centroids;
    public int GroupCount => _centroids.Length;

    private OpeningClusterModel(double[] means, double[] stds,
        double[][] centroids)
    {
        _means = means;
        _stds = stds;
        _centroids = centroids;
    }

    internal static OpeningClusterModel Fit(
        IReadOnlyList<OrbOpeningPatternExperiment.OpeningProfile> training,
        int groups)
    {
        if (training.Count < groups * 5)
            throw new ArgumentException("Need at least five training days per cluster.");
        var size = training[0].Features.Length;
        if (training.Any(x => x.Features.Length != size))
            throw new InvalidOperationException("Inconsistent opening feature vectors.");
        var means = new double[size];
        var stds = new double[size];
        for (var j = 0; j < size; j++)
        {
            means[j] = training.Average(p => p.Features[j]);
            stds[j] = Math.Sqrt(training.Average(
                p => Math.Pow(p.Features[j] - means[j], 2)));
            stds[j] = Math.Max(0.01, stds[j]);
        }
        var vectors = training.Select(p => Scale(p.Features, means, stds)).ToArray();
        var centroid = new List<double[]>();
        var origin = vectors.OrderBy(x => Distance(x, new double[size])).First();
        centroid.Add([.. origin]);
        while (centroid.Count < groups)
        {
            var farthest = vectors.OrderByDescending(x =>
                centroid.Min(c => Distance(x, c))).First();
            centroid.Add([.. farthest]);
        }
        var previous = Enumerable.Repeat(-1, vectors.Length).ToArray();
        for (var iteration = 0; iteration < 60; iteration++)
        {
            var membership = vectors.Select(p => Nearest(p, centroid)).ToArray();
            if (membership.SequenceEqual(previous)) break;
            previous = membership;
            for (var k = 0; k < groups; k++)
            {
                var members = vectors.Where((_, i) => membership[i] == k).ToArray();
                if (members.Length == 0)
                {
                    // Preserve the independent group rather than assign a
                    // fabricated centroid from any later evaluation period.
                    centroid[k] = [.. vectors.OrderByDescending(x =>
                        centroid.Min(c => Distance(x, c))).First()];
                    continue;
                }
                centroid[k] = Enumerable.Range(0, size)
                    .Select(j => members.Average(x => x[j])).ToArray();
            }
        }
        return new OpeningClusterModel(means, stds, centroid.ToArray());
    }

    internal int Assign(OrbOpeningPatternExperiment.OpeningProfile profile) =>
        Nearest(Scale(profile.Features, _means, _stds), _centroids);

    private static double[] Scale(double[] values, double[] means, double[] stds) =>
        values.Select((x, j) => Math.Clamp((x - means[j]) / stds[j], -3, 3))
            .ToArray();

    private static int Nearest(double[] point, IReadOnlyList<double[]> centers)
    {
        var winner = 0;
        var best = Distance(point, centers[0]);
        for (var i = 1; i < centers.Count; i++)
        {
            var d = Distance(point, centers[i]);
            if (d < best) { best = d; winner = i; }
        }
        return winner;
    }

    private static double Distance(double[] a, double[] b)
    {
        double distance = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var v = a[i] - b[i];
            distance += v * v;
        }
        return distance;
    }
}
