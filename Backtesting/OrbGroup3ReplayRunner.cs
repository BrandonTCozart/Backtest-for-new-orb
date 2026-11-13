using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

// Research replay of the previously observed Group 3 winner. DOES NOT search
// strategies, change its configuration, or apply validation no-trade gating.
// The original grouping is reconstructed exclusively from its original
// 2020-09-09..2023-09-05 training dates, then kept fixed for every replay.
public static class OrbGroup3ReplayRunner
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private static readonly DateOnly OriginalTrainingStart = new(2020, 9, 9);
    private static readonly DateOnly OriginalTrainingEnd = new(2023, 9, 5);
    private const int OriginalTrainingSessions = 735;
    private const int GroupId = 3;
    private const int GroupCount = 4;

    // Exact Group 3 winner from ORB_PATTERN_SELECTED (NOT reoptimized).
    // Reversed breakout+retest; 10-40 point OR width; Tue/Wed/Thu only;
    // stop risk $600 max, $500 fallback; 11:00 cutoff; $150 trail.
    internal static readonly MultiStrategyVariant FrozenVariant = new(
        Id: 14051, Kind: MultiStrategyKind.Orb, Lookback: 0,
        Threshold: 0m, StopAtrMultiple: 0m, RewardRisk: 0m,
        VolumeMultiple: 0m, OrbCutoffMinutes: 90, OrbTrailDollars: 150m,
        EventRule: EconomicEventRule.AvoidIntradayReleaseWindow,
        WeekdayMask: 14, OrbVwap: VwapRule.Off,
        OrbRangeMin: 10m, OrbRangeMax: 40m, OrbRetest: true,
        OrbRiskCap: 600m, OrbFallbackRisk: 500m,
        SignalMode: SignalMode.Reversed);

    internal sealed record Prepared(
        IReadOnlyList<MultiSession> Sessions,
        IReadOnlyList<SessionData> Orb);

    internal sealed record ReplaySample(
        string Source, IReadOnlyList<MultiSession> Sessions,
        IReadOnlyList<int> AssignedGroups,
        IReadOnlyList<MultiStrategyTrade> Trades,
        IReadOnlyList<MultiStrategyTrade> LegacyTrades)
    {
        public int GroupDays => AssignedGroups.Count(x => x == GroupId);
    }

    internal sealed record FrozenHistorical(
        OpeningClusterModel Model, OrbOpeningPatternExperiment.Split Split,
        ReplaySample Sample);

    public static async Task<int> RunAsync(
        string? connectionString, string symbol, string timeZone)
    {
        Console.WriteLine("=== FROZEN ORB GROUP 3 REPLAY - ALL SQL HISTORY (NO LIVE ORDERS) ===");
        Console.WriteLine("Only the previous 80.95% Group 3 setup, config 14051, Reversed.");
        Console.WriteLine("No grid search, no new clusters, no validation trade gate.");
        var progress = new OrbResearchProgress();
        progress.Start(1, "Load historical SQL (indeterminate)", 1);
        var historicalBars = await SqlMarketDataLoader.LoadAsync(connectionString,
            symbol, timeZone, tableName: "MES_DATA_2020_2025");
        progress.Complete();

        var historical = FitAndReplayHistorical(historicalBars.ActiveBars,
            historicalBars.AllContractBars, originalAnchorRequired: true,
            progress);

        // The newer source is read only after the historical model is frozen.
        progress.Start(5, "Load recent SQL (indeterminate)", 1);
        var recentBars = await SqlMarketDataLoader.LoadAsync(connectionString,
            symbol, timeZone, tableName: "MES_DATA");
        progress.Complete();

        var recent = ReplayRecent(historical.Model, historical.Sample,
            recentBars.ActiveBars, recentBars.AllContractBars, progress);

        progress.Start(7, "Report all periods and individual trades", 1);
        Report(historical, recent);
        progress.Complete();
        Console.WriteLine("Frozen Group 3 replay complete; read-only, no live orders.");
        return 0;
    }

    private static Prepared Prepare(IReadOnlyList<PriceBar> active,
        IReadOnlyList<PriceBar> all, OrbResearchProgress progress,
        bool startNewStage = true)
    {
        if (startNewStage)
            progress.Start(2, "Validate and align historical RTH/ORB sessions", 3);
        var complete = MultiStrategyData.Build(active, all, "unused");
        progress.Advance();
        var valid = complete.Select(x => x.Day).ToHashSet();
        var original = OptimizationData.Prepare(active.ToList(), all.ToList())
            .Where(x => x.Range is not null && valid.Contains(x.Day))
            .ToDictionary(x => x.Day);
        progress.Advance();
        var sessions = complete.Where(x => original.ContainsKey(x.Day)).ToArray();
        var orb = sessions.Select(x => original[x.Day]).ToArray();
        progress.Complete();
        Console.WriteLine("ORB_GROUP3_AUDIT,complete_sessions=" + complete.Count +
            ",usable_orb_sessions=" + sessions.Length +
            ",excluded_no_range=" + (complete.Count - sessions.Length));
        if (sessions.Length == 0)
            throw new InvalidOperationException("No complete ORB sessions in this SQL source.");
        return new Prepared(sessions, orb);
    }

    internal static FrozenHistorical FitAndReplayHistorical(
        IReadOnlyList<PriceBar> active, IReadOnlyList<PriceBar> all,
        bool originalAnchorRequired = true,
        OrbResearchProgress? progress = null)
    {
        progress ??= new OrbResearchProgress();
        var historical = Prepare(active, all, progress);
        var days = historical.Sessions;
        var split = OrbOpeningPatternExperiment.MakeSplit(days.Count);
        var trainingCount = split.TrainEnd;
        if (originalAnchorRequired)
        {
            // Refuse to silently call a different cluster "Group 3" if
            // historical source sessions were added/removed before its fit.
            if (days[0].Day != OriginalTrainingStart ||
                days.Count < OriginalTrainingSessions ||
                days[OriginalTrainingSessions - 1].Day != OriginalTrainingEnd ||
                split.TrainEnd != OriginalTrainingSessions)
                throw new InvalidOperationException(
                    "Original Group 3 training anchor differs from the prior " +
                    "experiment (735 sessions, 2020-09-09 through 2023-09-05). " +
                    "Frozen group identity cannot be guaranteed. Restore " +
                    "the original historical dataset before replay.");
            HistoricalReplayRunner.ValidateHistoricalReleaseCoverage(
                days[0].Day, days[^1].Day);
            trainingCount = OriginalTrainingSessions;
        }

        progress.Start(3, "Reconstruct original four groups from training only", 2);
        var features = OrbOpeningPatternExperiment.ExtractProfiles(days);
        progress.Advance();
        var model = OpeningClusterModel.Fit(
            features.Take(trainingCount).ToArray(), GroupCount);
        progress.Complete();

        progress.Start(4, "Replay Group 3 (corrected and legacy stops)", 2);
        var sample = Replay("MES_DATA_2020_2025", historical, model, progress);
        progress.Complete();
        return new FrozenHistorical(model, split, sample);
    }

    internal static ReplaySample ReplayRecent(
        OpeningClusterModel frozenModel, ReplaySample historical,
        IReadOnlyList<PriceBar> active, IReadOnlyList<PriceBar> all,
        OrbResearchProgress? progress = null)
    {
        progress ??= new OrbResearchProgress();
        progress.Start(6, "Prepare and replay frozen Group 3 on newer data", 5);
        var recent = Prepare(active, all, progress, startNewStage: false);
        if (recent.Sessions[0].Day <= historical.Sessions[^1].Day)
            throw new InvalidOperationException(
                "Recent SQL overlaps historical SQL; refusing double-counted trades.");
        var result = Replay("MES_DATA", recent, frozenModel, progress);
        progress.Complete();
        return result;
    }

    // All sessions are classified, but ONLY Group 3 sessions are simulated.
    // Other groups are not traded, scored or optimized in this replay.
    internal static ReplaySample Replay(
        string source, Prepared prepared, OpeningClusterModel frozenModel,
        OrbResearchProgress? progress = null)
    {
        var sessions = prepared.Sessions;
        var profiles = OrbOpeningPatternExperiment.ExtractProfiles(sessions);
        var groups = profiles.Select(frozenModel.Assign).ToArray();
        var selectedSessions = new List<MultiSession>();
        var selectedOrb = new List<SessionData>();
        for (var i = 0; i < sessions.Count; i++)
            if (groups[i] == GroupId)
            {
                selectedSessions.Add(sessions[i]);
                selectedOrb.Add(prepared.Orb[i]);
            }
        OptimizationEngine.ClearCaches();
        var corrected = MultiStrategyEngine.Generate(FrozenVariant,
                selectedSessions, selectedOrb,
                InvalidProtectiveStopPolicy.KeepLastValidStop)
            .Values.OrderBy(t => t.EntryAt).ToArray();
        progress?.Advance();
        // Replay the previous policy from the identical group assignments and
        // exact same fixed variant, so any difference is caused by stop handling.
        OptimizationEngine.ClearCaches();
        var legacy = MultiStrategyEngine.Generate(FrozenVariant,
                selectedSessions, selectedOrb,
                InvalidProtectiveStopPolicy.LegacyClose)
            .Values.OrderBy(t => t.EntryAt).ToArray();
        progress?.Advance();
        var groupDates = selectedSessions.Select(x => x.Day).ToHashSet();
        if (corrected.Concat(legacy).Any(t =>
                !groupDates.Contains(t.Day) || t.VariantId != FrozenVariant.Id))
            throw new InvalidOperationException(
                "Group 3 replay generated a trade outside Group 3.");
        if (corrected.Any(t => t.ExitReason == "invalid-stop-close"))
            throw new InvalidOperationException(
                "Corrected stop handling must never close at a fabricated invalid-stop price.");
        if (!corrected.Select(t => (t.Day, t.EntryAt, t.Side, t.Entry))
                .SequenceEqual(legacy.Select(t => (t.Day, t.EntryAt, t.Side, t.Entry))))
            throw new InvalidOperationException(
                "Changing stop policy must not change qualifying entry signals.");
        return new ReplaySample(source, sessions, groups, corrected, legacy);
    }

    internal static void Report(FrozenHistorical historical, ReplaySample recent)
    {
        var s = historical.Split;
        var older = historical.Sample;
        Console.WriteLine("ORB_GROUP3_FROZEN,group=3,groups=4,variant=14051,side=Reversed," +
            "training_start=" + Day(older.Sessions[0].Day) +
            ",training_end=" + Day(older.Sessions[s.TrainEnd - 1].Day) +
            ",variant_settings=" + FrozenVariant.Parameters);
        Console.WriteLine("ORB_GROUP3_STOP_POLICY,corrected=KeepLastValidStop," +
            "legacy=LegacyClose,config=14051,groups_frozen=true");
        const string heading = "period,source,start,end,valid_days,group3_days," +
            "trades,wins,win_rate,net_usd,max_drawdown_usd,profit_factor," +
            "net_at_20_cost,net_at_30_cost";
        Console.WriteLine("ORB_GROUP3_PERIOD," + heading);
        Console.WriteLine("ORB_GROUP3_LEGACY_PERIOD," + heading);

        void Period(string name, ReplaySample r, int start, int end, bool legacy)
        {
            var first = r.Sessions[start].Day;
            var last = r.Sessions[end - 1].Day;
            var eligible = r.AssignedGroups.Skip(start).Take(end - start)
                .Count(x => x == GroupId);
            var trades = (legacy ? r.LegacyTrades : r.Trades)
                .Where(t => t.Day >= first && t.Day <= last).ToArray();
            var score = MultiScore.From(trades);
            Console.WriteLine((legacy ? "ORB_GROUP3_LEGACY_PERIOD," :
                "ORB_GROUP3_PERIOD,") + name + "," + r.Source +
                "," + Day(first) + "," + Day(last) +
                "," + (end - start) + "," + eligible +
                "," + score.Trades + "," + score.Wins +
                "," + F(score.WinRate) + "," + F(score.Net) +
                "," + F(score.Drawdown) + "," +
                (score.ProfitFactor is { } pf ? F(pf) : "n/a") +
                "," + F(score.Net - 10m * score.Trades) +
                "," + F(score.Net - 20m * score.Trades));
        }
        foreach (var legacy in new[] { false, true })
        {
            Period("HISTORICAL_TRAIN_IN_SAMPLE", older, 0, s.TrainEnd, legacy);
            Period("HISTORICAL_VALIDATION", older, s.TrainEnd, s.ValidationEnd, legacy);
            Period("HISTORICAL_TEST_PREVIOUSLY_EXAMINED",
                older, s.ValidationEnd, s.Count, legacy);
            Period("HISTORICAL_ALL", older, 0, s.Count, legacy);
            Period("RECENT_FORWARD_RESEARCH",
                recent, 0, recent.Sessions.Count, legacy);
        }

        // Benchmark is the 80.95% result the user is asking to reproduce.
        // This is a diagnostic ONLY: Group 3 was chosen after this was seen.
        if (s.Count == 1225 &&
            older.Sessions[0].Day == OriginalTrainingStart &&
            older.Sessions[^1].Day == new DateOnly(2025, 9, 8))
        {
            var testStart = older.Sessions[s.ValidationEnd].Day;
            var score = MultiScore.From(
                older.LegacyTrades.Where(t => t.Day >= testStart));
            var correctedScore = MultiScore.From(
                older.Trades.Where(t => t.Day >= testStart));
            Console.WriteLine("ORB_GROUP3_REPRODUCTION,legacy_" +
                (score.Trades == 21 && score.Wins == 17 && score.Net == 2965m
                    ? "PASS" : "MISMATCH") +
                ",prior_test_trades=21,prior_test_wins=17,prior_test_net=2965.00" +
                ",actual_trades=" + score.Trades +
                ",actual_wins=" + score.Wins +
                ",actual_net=" + F(score.Net) +
                ",corrected_trades=" + correctedScore.Trades +
                ",corrected_wins=" + correctedScore.Wins +
                ",corrected_net=" + F(correctedScore.Net));
        }
        foreach (var legacy in new[] { false, true })
        {
            var allTrades = (legacy ? older.LegacyTrades : older.Trades)
                .Concat(legacy ? recent.LegacyTrades : recent.Trades)
                .OrderBy(t => t.EntryAt).ToArray();
            var all = MultiScore.From(allTrades);
            Console.WriteLine((legacy ? "ORB_GROUP3_LEGACY_COMBINED," :
                "ORB_GROUP3_COMBINED,") + "all_history_and_recent,days=" +
                (older.Sessions.Count + recent.Sessions.Count) +
                ",group3_days=" + (older.GroupDays + recent.GroupDays) +
                ",trades=" + all.Trades + ",wins=" + all.Wins +
                ",win_rate=" + F(all.WinRate) + ",net=" + F(all.Net) +
                ",drawdown=" + F(all.Drawdown) +
                ",net_at_20_cost=" + F(all.Net - 10m * all.Trades) +
                ",net_at_30_cost=" + F(all.Net - 20m * all.Trades));
        }

        const string yearHeading = "source,year,group3_days,trades,wins," +
            "win_rate,net_usd,max_drawdown_usd";
        Console.WriteLine("ORB_GROUP3_YEAR," + yearHeading);
        Console.WriteLine("ORB_GROUP3_LEGACY_YEAR," + yearHeading);
        foreach (var legacy in new[] { false, true })
            foreach (var set in new[] { older, recent })
                foreach (var bucket in set.Sessions.GroupBy(x => x.Day.Year))
                {
                    var yearTrades = (legacy ? set.LegacyTrades : set.Trades)
                        .Where(x => x.Day.Year == bucket.Key).ToArray();
                    var yearScore = MultiScore.From(yearTrades);
                    Console.WriteLine((legacy ? "ORB_GROUP3_LEGACY_YEAR," :
                        "ORB_GROUP3_YEAR,") + set.Source + "," + bucket.Key +
                        "," + bucket.Count(d =>
                            set.AssignedGroups[IndexOfDay(set.Sessions, d.Day)] == GroupId) +
                        "," + yearScore.Trades + "," + yearScore.Wins +
                        "," + F(yearScore.WinRate) + "," + F(yearScore.Net) +
                        "," + F(yearScore.Drawdown));
                }

        const string tradeHeading = "source,period,day,contract,side,entry_time_et," +
            "exit_time_et,entry,exit,exit_reason,net_usd";
        Console.WriteLine("ORB_GROUP3_TRADE," + tradeHeading);
        Console.WriteLine("ORB_GROUP3_LEGACY_TRADE," + tradeHeading);
        foreach (var legacy in new[] { false, true })
            foreach (var (set, isHistorical) in new[] { (older, true), (recent, false) })
                foreach (var trade in legacy ? set.LegacyTrades : set.Trades)
                {
                    var idx = isHistorical ? IndexOfDay(older.Sessions, trade.Day) : 0;
                    var period = !isHistorical ? "RECENT_FORWARD" :
                        idx < s.TrainEnd ? "HISTORICAL_TRAIN" :
                        idx < s.ValidationEnd ? "HISTORICAL_VALIDATION" :
                        "HISTORICAL_TEST";
                    Console.WriteLine((legacy ? "ORB_GROUP3_LEGACY_TRADE," :
                        "ORB_GROUP3_TRADE,") + set.Source + "," + period +
                        "," + Day(trade.Day) + "," + trade.Contract + "," + trade.Side +
                        "," + Et(trade.EntryAt) + "," + Et(trade.ExitAt) +
                        "," + F(trade.Entry) + "," + F(trade.Exit) +
                        "," + trade.ExitReason + "," + F(trade.Net));
                }
        Console.WriteLine("RESEARCH CAVEAT: Corrected stop replay ignores a proposed " +
            "protective stop on the wrong side of the latest candle close; " +
            "it retains the prior stop and retries the stage on later candles. " +
            "LegacyClose figures retain the old artificial candle-close exit. " +
            "Historical 80.95% was post-selection; neither series is an " +
            "independent unseen live result. No real trades or SQL writes.");
    }

    private static int IndexOfDay(IReadOnlyList<MultiSession> sessions, DateOnly day)
    {
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
        throw new InvalidOperationException("Trade date not found in replay calendar.");
    }

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", Ci);
    private static string Et(DateTimeOffset d) =>
        MarketTime.ToEastern(d).ToString("yyyy-MM-ddTHH:mm:sszzz", Ci);
    private static string F(decimal d) => d.ToString("0.00", Ci);
}
