using Orb;

namespace Backtest_for_new_orb;

// Dependency-free synthetic checks: dotnet run -- --self-test
// No credentials, external test packages, SQL Server or future market data.
public static class MultiStrategySelfTests
{
    public static int Run()
    {
        var folder = Path.Combine(Path.GetTempPath(),
            "es-multi-strategy-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var date = new DateOnly(2026, 9, 1);
            var bars = CreateFullSession(date);
            var audited = MultiStrategyData.Build(bars, bars, folder);
            Ensure(audited.Count == 1, "A complete RTH day must be accepted.");
            var session = audited[0];
            Ensure(session.Rth.Length == 390, "RTH must have 390 one-minute bars.");
            Ensure(session.Overnight?.Length == 930, "Overnight must have 930 bars.");
            Ensure(session.Overnight![^1].T.AddMinutes(1) == session.Rth[0].T,
                "Overnight must end exactly when RTH begins.");

            var oneMinuteGap = bars.Where(b => b.T != session.Rth[100].T).ToList();
            Ensure(MultiStrategyData.Build(oneMinuteGap, oneMinuteGap, folder).Count == 0,
                "Any missing regular-session minute must exclude the day.");
            var missingNight = bars.Where(b => b.T != session.Overnight[200].T).ToList();
            var nightAudit = MultiStrategyData.Build(missingNight, missingNight, folder);
            Ensure(nightAudit.Count == 1 && nightAudit[0].Overnight is null,
                "Missing overnight minutes must exclude only overnight trading.");
            Ensure(nightAudit[0].Rth.Length == 390,
                "RTH stays valid even if an overnight minute is absent.");

            var mutated = bars.Select(b => b.T == session.Rth[300].T
                ? b with { C = new Candle {
                    T = b.T, O = b.C.O, H = b.C.H + 30m,
                    L = b.C.L, C = b.C.C, V = b.C.V * 5
                } }
                : b).ToList();
            var future = MultiStrategyData.Build(mutated, mutated, folder).Single();
            Ensure(session.OpeningWidth == future.OpeningWidth &&
                   session.EarlyAtr == future.EarlyAtr &&
                   session.EarlyVolumeRatio == future.EarlyVolumeRatio &&
                   session.OpeningMove == future.OpeningMove,
                   "09:45 routing features must not depend on later candles.");
            Ensure(session.AfternoonMove == future.AfternoonMove &&
                   session.AfternoonAtr == future.AfternoonAtr,
                   "13:00 routing features must not depend on post-13:00 candles.");

            var variants = MultiStrategyEngine.EnumerateVariants(MultiSearchProfile.Smoke);
            Ensure(variants.Count == variants.Select(x => x.Id).Distinct().Count() &&
                   variants.Count == 2040,
                "Smoke must preserve 440 fixed + 800 old trails and sample each new exit mode.");
            var deepFolds = MultiStrategyRunner.CreateResearchFolds(249);
            Ensure(deepFolds.Length == 5 &&
                   deepFolds[0].TrainingEnd == 99 &&
                   deepFolds[0].TestStart == 99 &&
                   deepFolds[^1].TestEnd == 249 &&
                   deepFolds.All(f => f.TrainingEnd == f.TestStart),
                "Deep walk-forward must have five chronological expanding training/test blocks.");
            for (var i = 1; i < deepFolds.Length; i++)
                Ensure(deepFolds[i].TestStart == deepFolds[i - 1].TestEnd &&
                       deepFolds[i].TrainingEnd > deepFolds[i - 1].TrainingEnd,
                    "Five forward test blocks must be contiguous, nonoverlapping and chronological.");
            var deepVariants = MultiStrategyEngine.EnumerateVariants(MultiSearchProfile.Deep);
            Ensure(deepVariants.Count == deepVariants.Select(x => x.Id).Distinct().Count() &&
                   deepVariants.Count == 587520,
                "Deep grid preserves 74,560 fixed and 58,624 legacy trailing IDs plus new exits.");
            Ensure(deepVariants.Take(74560).All(v =>
                        v.ExitMode == MultiExitMode.FixedTarget) &&
                    deepVariants.Skip(74560).Take(58624).All(v =>
                        v.ExitMode == MultiExitMode.TrailingStop &&
                        v.RewardRisk == 0m &&
                        v.TrailActivationR is 0.5m or 1m &&
                        v.TrailAtrMultiple is 0.5m or 1m) &&
                    Enum.GetValues<MultiStrategyKind>()
                        .Where(k => k != MultiStrategyKind.Orb)
                        .All(k => Enum.GetValues<MultiExitMode>().All(exit =>
                            deepVariants.Any(v => v.Kind == k && v.ExitMode == exit))) &&
                    deepVariants.Any(v => v.TrailActivationR == 3m) &&
                    deepVariants.Any(v => v.TrailAtrMultiple == 3m) &&
                    deepVariants.Any(v => v.TrailBreakeven) &&
                    deepVariants.Any(v => v.TargetLookbackBars == 60),
                "Preserve original IDs; cover expanded trails, structure, stages and hybrid exits.");
            Ensure(Enum.GetValues<MultiStrategyKind>().All(k =>
                deepVariants.Any(v => v.Kind == k)),
                "Deep grid must cover all seven strategy families.");
            Ensure(Enum.GetValues<MultiStrategyKind>().Length == 19 &&
                   Enum.GetValues<MultiStrategyKind>().All(
                       k => variants.Any(v => v.Kind == k)),
                "All 19 price-action and legacy strategy families need smoke candidates.");

            foreach (var variant in variants.Where(v => v.Kind != MultiStrategyKind.Orb))
            {
                var result = MultiStrategyEngine.GenerateDay(variant, session);
                if (result is null) continue;
                Ensure(result.EntryAt < result.ExitAt,
                    "Trade cannot exit before or at its entry time.");
                Ensure(result.InitialRiskDollars <= 600m,
                    "Initial risk must respect the hard $600 ES cap.");
                Ensure(result.Costs == 10m && result.Quantity == 1,
                    "New strategies should use 1 ES and the shared cost assumption.");
                var exit = MarketTime.ToEastern(result.ExitAt);
                if (variant.Slot == MultiStrategySlot.Morning)
                    Ensure(exit.TimeOfDay <= new TimeSpan(11, 30, 0),
                        "Morning positions must close by 11:30 ET.");
                if (variant.Slot == MultiStrategySlot.Afternoon)
                    Ensure(exit.TimeOfDay <= new TimeSpan(15, 55, 0),
                        "Afternoon positions must close by 15:55 ET.");
                if (variant.Slot == MultiStrategySlot.Overnight)
                    Ensure(exit.TimeOfDay <= new TimeSpan(8, 0, 0),
                        "Overnight positions must close by 08:00 ET.");
            }
            foreach (var variant in variants.Where(v =>
                v.Kind is MultiStrategyKind.MorningMomentum or
                    MultiStrategyKind.MorningVwapReversion or
                    MultiStrategyKind.OvernightMomentum or
                    MultiStrategyKind.OvernightReversion))
            {
                var before = MultiStrategyEngine.GenerateDay(variant, session);
                var after = MultiStrategyEngine.GenerateDay(variant, future);
                Ensure(Equals(before, after),
                    "Morning and overnight trades must be independent of later RTH bars.");
            }

            TestTrailingStops(session);
            TestNewExitModes(session);
            TestPriceActionSignals(date, folder);
            TestFallbackRouting(date, session, variants);

            var start = MarketTime.ToUtc(date, new TimeOnly(11, 0));
            var win = new MultiStrategyTrade(date, MultiStrategyKind.MorningMomentum, 1,
                "ESU6", TradeDirection.Long, start, start.AddMinutes(1),
                5000m, 5004m, 500m, "target");
            var loss = new MultiStrategyTrade(date, MultiStrategyKind.MorningMomentum, 1,
                "ESU6", TradeDirection.Long, start.AddMinutes(2), start.AddMinutes(3),
                5000m, 4998m, 500m, "stop");
            var score = MultiScore.From(new[] { win, loss });
            Ensure(score.Trades == 2 && score.Wins == 1 &&
                   score.Gross == 100m && score.Costs == 20m &&
                   score.Net == 80m && score.Drawdown == 110m &&
                   score.WinRate == 50m,
                   "P&L, transaction costs, win rate and closed-trade drawdown must reconcile.");

            Ensure(EconomicCalendar.Events(new DateOnly(2025, 10, 24)).Any(e => e.Code == "CPI") &&
                   EconomicCalendar.Events(new DateOnly(2026, 8, 7)).Any(e => e.Code == "NFP"),
                "Historical economic-calendar coverage should not silently disappear.");
            Ensure(EconomicCalendar.Events(new DateOnly(2023, 7, 12))
                       .Any(e => e.Code == "CPI") &&
                   EconomicCalendar.Events(new DateOnly(2024, 2, 29))
                       .Any(e => e.Code == "PCE") &&
                   EconomicCalendar.Events(new DateOnly(2024, 1, 3))
                       .Any(e => e.Code == "ISM_MANUFACTURING") &&
                   EconomicCalendar.Events(new DateOnly(2022, 1, 27))
                       .Any(e => e.Code == "GDP") &&
                   EconomicCalendar.Events(new DateOnly(2023, 6, 15))
                       .Any(e => e.Code == "RETAIL_SALES") &&
                   EconomicCalendar.IsFedDecisionDay(new DateOnly(2024, 9, 18)) &&
                   EconomicCalendar.IsBeforeFedDecision(new DateOnly(2024, 9, 17)),
                "Official 2020–2025 GDP/PCE/CPI/retail/FOMC dates and ISM rule dates must be loaded.");
            // Include the real replay's partial boundary years, not just 2021-2024.
            HistoricalReplayRunner.ValidateHistoricalReleaseCoverage(
                new DateOnly(2020, 9, 8), new DateOnly(2025, 9, 8));
            Ensure(EconomicCalendar.Events(new DateOnly(2020, 10, 7))
                       .Any(e => e.Code == "FOMC_MINUTES" &&
                                 e.TimeEt == TimeSpan.FromHours(14)) &&
                   EconomicCalendar.Events(new DateOnly(2020, 11, 25))
                       .Any(e => e.Code == "FOMC_MINUTES") &&
                   EconomicCalendar.Events(new DateOnly(2021, 1, 6))
                       .Any(e => e.Code == "FOMC_MINUTES"),
                "Archived 2020 FOMC minutes must remain available for intraday rules.");
            var frameworkDay = new DateOnly(2025, 8, 22);
            Ensure(!EconomicCalendar.IsFedDecisionDay(frameworkDay) &&
                   !EconomicCalendar.IsBeforeFedDecision(new DateOnly(2025, 8, 21)) &&
                   EconomicCalendar.Events(frameworkDay).Any(e =>
                       e.Code == "FOMC_FRAMEWORK" &&
                       e.TimeEt == TimeSpan.FromHours(10)) &&
                   !EconomicCalendar.AllowsEntry(EconomicEventRule.AvoidIntradayReleaseWindow,
                       frameworkDay, MarketTime.ToUtc(frameworkDay, new TimeOnly(10, 0))),
                "The Aug 22 2025 framework update must not count as a rate decision.");
            Ensure(!EconomicCalendar.AllowsEntry(
                EconomicEventRule.AvoidMajorReleaseDay, new DateOnly(2024, 2, 29),
                MarketTime.ToUtc(new DateOnly(2024, 2, 29), new TimeOnly(10, 0))),
                "Archived PCE releases must suppress avoid-major-day entries.");
            Ensure(!EconomicCalendar.AllowsEntry(
                EconomicEventRule.AvoidFomcDayAndPriorTradingDay,
                new DateOnly(2024, 9, 17),
                MarketTime.ToUtc(new DateOnly(2024, 9, 17), new TimeOnly(10, 0))),
                "Archived FOMC decisions must suppress the prior trading day.");

            // End-to-end dry run: 25 complete synthetic ES sessions exercise the
            // entire candidate grid, all three walk-forward folds, causal router,
            // CSV serialization and sequential no-overlap portfolio construction.
            // No database is accessed, and no orders or network requests occur.
            var sampleDays = Enumerable.Range(0, 36)
                .Select(i => new DateOnly(2026, 8, 3).AddDays(i))
                .Where(d => d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                .Take(25).ToArray();
            var syntheticYear = sampleDays.SelectMany(CreateFullSession)
                .OrderBy(b => b.T).ToList();
            // Capture the ordinary console report instead of checking report
            // artifacts: the backtest must not write any files or need them.
            var regularOut = Console.Out;
            using var captured = new StringWriter();
            int status;
            try
            {
                Console.SetOut(captured);
                status = MultiStrategyRunner.Run(syntheticYear, syntheticYear,
                    folder, MultiSearchProfile.Smoke);
            }
            finally
            {
                Console.SetOut(regularOut);
            }
            Ensure(status == 0,
                "Complete synthetic multi-strategy backtest should return success.");
            var terminal = captured.ToString();
            Ensure(terminal.Contains("WEEKLY_OOS,week_start_et,week_end_et,strategy,net_usd,cumulative_net_usd,trades"),
                "Weekly terminal output must have the expected graph-ready columns.");
            var weeklyLines = terminal.Split('\n')
                .Where(line => line.StartsWith("WEEKLY_OOS,", StringComparison.Ordinal))
                .ToArray();
            Ensure(weeklyLines.Length > Enum.GetValues<MultiStrategyKind>().Length &&
                   weeklyLines.Any(x => x.Contains(",SelectedPortfolio,")),
                "Console must contain weekly data for all strategies and the selected portfolio.");
            Ensure(!Directory.Exists(folder) ||
                   !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(),
                "Multi-strategy execution must not create any CSV/text report files.");
            var totalLines = terminal.Split('\n')
                .Where(line => line.StartsWith("TOTAL_PNL,", StringComparison.Ordinal))
                .ToArray();
            Ensure(totalLines.Length == Enum.GetValues<MultiStrategyKind>().Length + 2 &&
                   totalLines.Any(x => x.StartsWith("TOTAL_PNL,SelectedPortfolio,")),
                "All strategy and portfolio in-sample-plus-OOS totals must be printed.");
            foreach (var line in totalLines.Skip(1))
            {
                var cells = line.Trim().Split(',');
                Ensure(cells.Length == 8,
                    "Each total P&L line must have eight graph/analysis-ready columns.");
                var training = decimal.Parse(cells[2],
                    System.Globalization.CultureInfo.InvariantCulture);
                var outOfSample = decimal.Parse(cells[3],
                    System.Globalization.CultureInfo.InvariantCulture);
                var full = decimal.Parse(cells[4],
                    System.Globalization.CultureInfo.InvariantCulture);
                Ensure(full == training + outOfSample,
                    "Full-history net P&L must exactly equal disjoint in-sample prefix plus OOS.");
            }

            TestWeeklyTotals(date);
            Ensure(SqlMarketDataLoader.ValidateTableName("MES_DATA") == "MES_DATA" &&
                   SqlMarketDataLoader.ValidateTableName("MES_DATA_2020_2025") ==
                       "MES_DATA_2020_2025",
                "Both historical replay SQL table names must be supported.");
            try
            {
                _ = SqlMarketDataLoader.ValidateTableName("MES_DATA];DROP TABLE Users;--");
                throw new InvalidOperationException(
                    "Untrusted SQL identifier was unexpectedly accepted.");
            }
            catch (ArgumentException) { }

            // A fresh combined-run replay must use live training-selected
            // settings/policy, never the old hard-coded strategy IDs.
            var selectedKinds = Enum.GetValues<MultiStrategyKind>()
                .ToDictionary(kind => kind, kind =>
                {
                    var variant = deepVariants.Last(v => v.Kind == kind);
                    var sample = new MultiStrategyTrade(date, kind, variant.Id,
                        session.Contract, TradeDirection.Long,
                        session.Rth[40].T, session.Rth[41].T,
                        100m, 102m, 110m, "target");
                    var score = MultiScore.From(new[] { sample });
                    return new MultiStrategyRunner.Choice(variant, score,
                        new Dictionary<DateOnly, MultiStrategyTrade>(), score.Net,
                        score, score);
                });
            var dynamicPolicy = new MultiPortfolioPolicy(
                MultiPortfolioMode.Single, MultiStrategyKind.MorningMomentum);
            var fresh = HistoricalReplayRunner.FromSelection(
                date, date, selectedKinds, dynamicPolicy);
            Ensure(fresh.Choices.Count == 19 &&
                   fresh.SelectedPolicy == dynamicPolicy &&
                   fresh.Choices.All(x =>
                       x.Value.Variant.Id == selectedKinds[x.Key].Variant.Id) &&
                   fresh.Choices[MultiStrategyKind.MorningVwapReversion]
                       .Variant.ExitMode == MultiExitMode.TargetThenTrail,
                "Combined replay must capture freshly optimized exit settings and router.");

            // Regression of frozen historical replay: no SQL, no new fit on
            // historical candles, no file outputs, full weekly reconciliation.
            var frozen = HistoricalReplayRunner.BuildPlan(
                syntheticYear, syntheticYear);
            Ensure(frozen.Choices.Count == 19 &&
                   frozen.Choices.All(x =>
                       x.Value.Variant.Id == HistoricalReplayRunner.PinnedIds[x.Key]),
                "Replay must resolve every pinned Deep-grid fold winner unchanged.");
            Console.WriteLine("PINNED_EVENT_RULES: " + string.Join(", ",
                frozen.Choices.OrderBy(x => x.Key)
                    .Select(x => x.Key + "=" + x.Value.Variant.EventRule)));
            var summaryWriter = Console.Out;
            using var historyCaptured = new StringWriter();
            try
            {
                Console.SetOut(historyCaptured);
                Ensure(HistoricalReplayRunner.Replay(frozen,
                    syntheticYear, syntheticYear) == 0,
                    "Frozen historical replay must run to completion.");
            }
            finally
            {
                Console.SetOut(summaryWriter);
            }
            var replayOutput = historyCaptured.ToString();
            Ensure(replayOutput.Split('\n').Count(x =>
                       x.StartsWith("REPLAY_TOTAL,", StringComparison.Ordinal)) == 22 &&
                   replayOutput.Split('\n').Count(x =>
                       x.StartsWith("WEEKLY_REPLAY,", StringComparison.Ordinal)) > 21 &&
                   replayOutput.Contains("REPLAY_ROUTER,Fixed_best_per_session,") &&
                   replayOutput.Contains("REPLAY_ROUTER,Fixed_best_per_session_fallback,") &&
                   replayOutput.Contains("YEARLY_REPLAY,year,strategy,net_usd,trades,win_rate_pct") &&
                   replayOutput.Contains(",SelectedPortfolio,") &&
                   replayOutput.Contains(",FallbackPortfolio,"),
                "Historical replay should output each strategy and both router policies.");
            Ensure(!Directory.Exists(folder) ||
                   !Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(),
                "Frozen historical replay must not write CSV or text files.");

            Console.WriteLine("PASS: full 25-session synthetic walk-forward and terminal-only reporting.");
            Console.WriteLine("PASS: synthetic full/missing RTH and overnight minute coverage,");
            Console.WriteLine("      lookahead boundary checks, trade risk/close-time invariants,");
            Console.WriteLine("      nineteen strategy candidate families, P&L and event-calendar checks.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("MULTI-STRATEGY SELF-TEST FAILED: " + error);
            return 1;
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }


    private static void TestTrailingStops(MultiSession session)
    {
        // Synthetic 1-minute bars with an intentionally ambiguous activation
        // minute: a trail computed from the high of minute 31 must NOT be
        // executed against that same minute's low.
        var bars = session.Rth.Select(bar => bar with { C = new Candle {
            T = bar.T, O = 100m, H = 100.25m, L = 99.75m,
            C = 100m, V = 100
        } }).ToArray();
        void Set(int i, decimal open, decimal high, decimal low, decimal close)
        {
            bars[i] = bars[i] with { C = new Candle {
                T = bars[i].T, O = open, H = high,
                L = low, C = close, V = 100
            } };
        }
        Set(31, 100.25m, 102m, 100m, 101.75m);
        Set(32, 101.75m, 102m, 101.25m, 101.5m);
        Set(33, 101.5m, 101.75m, 100.75m, 101m);
        var fixedVariant = new MultiStrategyVariant(
            99001, MultiStrategyKind.MorningVwapReversion,
            10, 0.5m, 1m, 1m, 0m, 0, 0m, EconomicEventRule.Off);
        var tight = fixedVariant with {
            Id = 99002, RewardRisk = 0m,
            ExitMode = MultiExitMode.TrailingStop,
            TrailActivationR = 1m, TrailAtrMultiple = 0.5m
        };
        var wide = tight with { Id = 99003, TrailAtrMultiple = 1m };
        var delayed = tight with { Id = 99004, TrailActivationR = 2.5m };
        MultiStrategyTrade? Trade(MultiStrategyVariant v) =>
            MultiStrategyEngine.SimulateBracket(v, session, bars,
                30, 35, TradeDirection.Long, 1m);

        var hard = Trade(fixedVariant);
        var t = Trade(tight);
        var w = Trade(wide);
        var d = Trade(delayed);
        Ensure(hard is { ExitReason: "target", Exit: 101m } &&
               hard.ExitAt == bars[31].T.AddMinutes(1),
            "Fixed profit target must exit on target hit.");
        Ensure(t is { ExitReason: "trailing_stop", Exit: 101.5m } &&
               t.ExitAt == bars[32].T.AddMinutes(1),
            "Tight trailing stop only activates at bar 31 CLOSE and fills on bar 32.");
        Ensure(w is { ExitReason: "trailing_stop", Exit: 101m } &&
               w.ExitAt == bars[33].T.AddMinutes(1),
            "Wider ATR trail must survive the earlier pullback.");
        Ensure(d is { ExitReason: "time_exit" },
            "Delayed activation must not create a trailing stop before threshold.");

        var shortBars = bars.Select(bar => bar with { C = new Candle {
            T = bar.T, O = 100m, H = 100.25m, L = 99.75m,
            C = 100m, V = 100
        } }).ToArray();
        shortBars[31] = shortBars[31] with { C = new Candle {
            T = shortBars[31].T, O = 99.75m, H = 100m, L = 98m,
            C = 98.25m, V = 100
        } };
        shortBars[32] = shortBars[32] with { C = new Candle {
            T = shortBars[32].T, O = 98.25m, H = 98.75m, L = 98m,
            C = 98.5m, V = 100
        } };
        var shortTrade = MultiStrategyEngine.SimulateBracket(tight,
            session, shortBars, 30, 35, TradeDirection.Short, 1m);
        Ensure(shortTrade is { ExitReason: "trailing_stop", Exit: 98.5m } &&
               shortTrade.ExitAt == shortBars[32].T.AddMinutes(1),
            "Short trailing stops must trail the LOW and only fill on the next bar.");

        Console.WriteLine("PASS: fixed target vs early/delayed, wide/tight and short trailing exits.");
    }

    private static void TestNewExitModes(MultiSession session)
    {
        var bars = session.Rth.Select(bar => bar with { C = new Candle {
            T = bar.T, O = 100m, H = 100.25m, L = 99.75m,
            C = 100m, V = 100
        } }).ToArray();
        void Set(int i, decimal open, decimal high, decimal low, decimal close)
        {
            bars[i] = bars[i] with { C = new Candle {
                T = bars[i].T, O = open, H = high, L = low, C = close, V = 100
            } };
        }
        Set(29, 100m, 101.25m, 99.75m, 100m);
        Set(31, 100.25m, 102m, 100m, 101.75m);
        Set(32, 101.75m, 102m, 101.25m, 101.5m);
        Set(33, 101.5m, 101.75m, 100.75m, 101m);
        var v = new MultiStrategyVariant(99501,
            MultiStrategyKind.MorningVwapReversion, 10, 0.5m,
            1m, 1m, 0m, 0, 0m, EconomicEventRule.Off);
        MultiStrategyTrade? Trade(MultiStrategyVariant config) =>
            MultiStrategyEngine.SimulateBracket(config, session, bars, 30, 35,
                TradeDirection.Long, 1m);
        var structure = v with {
            ExitMode = MultiExitMode.StructureTarget, TargetLookbackBars = 20
        };
        var level = MultiStrategyEngine.StructuralTarget(
            structure, session, bars, 30, TradeDirection.Long, 100m, 1m, null);
        Ensure(level == 101.25m &&
               Trade(structure) is { ExitReason: "structure_target", Exit: 101.25m },
            "Structure target must use a past-only actual resistance level.");
        var changed = bars.ToArray();
        changed[32] = changed[32] with { C = new Candle {
            T = changed[32].T, O = 101.75m, H = 150m, L = 90m,
            C = 101.5m, V = 100
        } };
        Ensure(MultiStrategyEngine.StructuralTarget(structure, session, changed,
            30, TradeDirection.Long, 100m, 1m, null) == level,
            "Future candles must never repaint a structural target.");

        var hybrid = v with {
            ExitMode = MultiExitMode.TargetThenTrail, RewardRisk = 1m,
            TrailActivationR = 1m, TrailAtrMultiple = 0.5m
        };
        var staged = v with {
            ExitMode = MultiExitMode.StagedProfitLock, RewardRisk = 0m,
            TrailAtrMultiple = 0.5m
        };
        Ensure(Trade(hybrid) is { ExitReason: "trailing_stop", Exit: 101.5m } &&
               Trade(staged) is { ExitReason: "trailing_stop", Exit: 101.5m },
            "Milestones must activate, not immediately exit, hybrid/staged trails.");

        var breakEven = v with {
            ExitMode = MultiExitMode.TrailingStop, RewardRisk = 0m,
            TrailActivationR = 3m, TrailAtrMultiple = 2m,
            TrailBreakeven = true
        };
        Ensure(Trade(breakEven) is { ExitReason: "trailing_stop", Exit: 100m },
            "Break-even stop must activate after 1R and fill conservatively on a gap.");
        Console.WriteLine("PASS: structural level no-lookahead, hybrid, staged, break-even exits.");
    }

    private static void TestFallbackRouting(
        DateOnly day, MultiSession session,
        IReadOnlyList<MultiStrategyVariant> variants)
    {
        var primary = variants.First(v => v.Kind == MultiStrategyKind.MorningMomentum);
        var alternate = variants.First(v => v.Kind == MultiStrategyKind.MorningLiquiditySweep);
        var begin = MarketTime.ToUtc(day, new TimeOnly(10, 0));
        MultiStrategyTrade Trade(MultiStrategyVariant v, int entryMinutes) =>
            new(day, v.Kind, v.Id, session.Contract, TradeDirection.Long,
                begin.AddMinutes(entryMinutes),
                begin.AddMinutes(entryMinutes + 3),
                5000m, 5002m, 100m, "target");
        var laterPrimary = Trade(primary, 30);
        var earlyAlternate = Trade(alternate, 5);
        MultiStrategyRunner.Choice BuildChoice(
            MultiStrategyVariant v, MultiStrategyTrade reportTrade,
            IReadOnlyDictionary<DateOnly, MultiStrategyTrade> available,
            decimal robustness)
        {
            var score = MultiScore.From(new[] { reportTrade });
            return new MultiStrategyRunner.Choice(v, score, available,
                robustness, score, score);
        }
        var noPrimary = new Dictionary<MultiStrategyKind, MultiStrategyRunner.Choice>
        {
            [primary.Kind] = BuildChoice(primary, laterPrimary,
                new Dictionary<DateOnly, MultiStrategyTrade>(), 200m),
            [alternate.Kind] = BuildChoice(alternate, earlyAlternate,
                new Dictionary<DateOnly, MultiStrategyTrade> {
                    [day] = earlyAlternate
                }, 100m)
        };
        var fixedBase = new MultiPortfolioPolicy(MultiPortfolioMode.FixedSessions);
        var fixedFallback = fixedBase with { UseFallbacks = true };
        var firstBase = MultiStrategyRunner.EvaluatePortfolio(fixedBase,
            noPrimary, new[] { session }, 0, 1);
        var firstFallback = MultiStrategyRunner.EvaluatePortfolio(fixedFallback,
            noPrimary, new[] { session }, 0, 1);
        Ensure(firstBase.Score.Trades == 0 &&
               firstFallback.Score.Trades == 1 &&
               firstFallback.Trades[0].Kind == alternate.Kind &&
               firstFallback.FallbackTrades == 1,
            "Fallback must fill a valid alternate signal when the primary has none.");

        var withPrimary = new Dictionary<MultiStrategyKind, MultiStrategyRunner.Choice>(
            noPrimary)
        {
            [primary.Kind] = BuildChoice(primary, laterPrimary,
                new Dictionary<DateOnly, MultiStrategyTrade> { [day] = laterPrimary },
                200m)
        };
        var timeFirst = MultiStrategyRunner.EvaluatePortfolio(fixedFallback,
            withPrimary, new[] { session }, 0, 1);
        var original = MultiStrategyRunner.EvaluatePortfolio(fixedBase,
            withPrimary, new[] { session }, 0, 1);
        Ensure(timeFirst.Score.Trades == 1 &&
               timeFirst.Trades[0].Kind == alternate.Kind &&
               timeFirst.FallbackTrades == 1 &&
               original.Trades.Single().Kind == primary.Kind,
            "Chronological fallback may enter only when its signal occurs first; no hindsight.");

        var tiePrimary = Trade(primary, 5);
        withPrimary[primary.Kind] = BuildChoice(primary, tiePrimary,
            new Dictionary<DateOnly, MultiStrategyTrade> { [day] = tiePrimary },
            200m);
        var tie = MultiStrategyRunner.EvaluatePortfolio(fixedFallback,
            withPrimary, new[] { session }, 0, 1);
        Ensure(tie.Score.Trades == 1 &&
               tie.Trades[0].Kind == primary.Kind && tie.FallbackTrades == 0,
            "When two strategies signal simultaneously, training-ranked primary wins.");

        var single = MultiStrategyRunner.EvaluatePortfolio(
            new MultiPortfolioPolicy(MultiPortfolioMode.Single, primary.Kind,
                UseFallbacks: true), noPrimary, new[] { session }, 0, 1);
        Ensure(single.Score.Trades == 0 && single.FallbackTrades == 0,
            "Standalone single-strategy policies must remain single-strategy.");

        var adaptive = MultiStrategyRunner.EvaluatePortfolio(
            new MultiPortfolioPolicy(MultiPortfolioMode.AdaptiveRegimes,
                OrbThreshold: -1m, MorningThreshold: -1m, UseFallbacks: true),
            noPrimary, new[] { session }, 0, 1);
        Ensure(session.EarlyAtr > 0m && adaptive.Score.Trades == 1 &&
               adaptive.Trades[0].Kind == alternate.Kind &&
               adaptive.FallbackTrades == 1,
            "Adaptive trend-regime strategy must be able to fall back to another morning family.");
    }

    private static void TestWeeklyTotals(DateOnly date)
    {
        // Monday 2026-08-31; first and last weeks are included even with
        // no orders. Sums of per-week net and cumulative net must reconcile.
        var monday = new DateOnly(2026, 8, 31);
        var trades = Enum.GetValues<MultiStrategyKind>().ToDictionary(
            k => k, _ => new List<MultiStrategyTrade>());
        var kind = MultiStrategyKind.MorningMomentum;
        var firstEntry = MarketTime.ToUtc(monday, new TimeOnly(10, 0));
        var secondEntry = MarketTime.ToUtc(monday.AddDays(8), new TimeOnly(10, 0));
        trades[kind].Add(new MultiStrategyTrade(monday, kind, 1, "ESU6",
            TradeDirection.Long, firstEntry, firstEntry.AddMinutes(1),
            5000m, 5002m, 110m, "target")); // net +$90
        trades[kind].Add(new MultiStrategyTrade(monday.AddDays(8), kind, 1, "ESU6",
            TradeDirection.Long, secondEntry, secondEntry.AddMinutes(1),
            5000m, 4999m, 60m, "stop")); // net -$60
        var rows = MultiStrategyRunner.BuildWeeklyOosRows(
            trades, trades[kind], monday, monday.AddDays(20));
        var momentum = rows.Where(x => x.Strategy == kind.ToString()).ToArray();
        Ensure(momentum.Length == 3 &&
               momentum[0].WeekStart == monday &&
               momentum[0].WeekEnd == monday.AddDays(6) &&
               momentum[0].Net == 90m &&
               momentum[1].Net == -60m &&
               momentum[2].Net == 0m &&
               momentum[^1].Cumulative == 30m,
            "Weekly series must use Monday-Sunday dates, zero-fill, and cumulative net P&L.");
        Ensure(rows.Where(x => x.Strategy == "SelectedPortfolio").Sum(x => x.Net) == 30m &&
               momentum.Sum(x => x.Net) == MultiScore.From(trades[kind]).Net,
            "Every weekly series must reconcile to stitched OOS trade-level net profit.");
    }

    // Every new family must actually produce a signal on a deterministic
    // 1-minute fixture in BOTH regular-session slots. Also validates that
    // a later extreme does not repaint earlier levels or Fibonacci pivots.
    private static void TestPriceActionSignals(DateOnly date, string folder)
    {
        var pairs = new (MultiStrategyKind Morning, MultiStrategyKind Afternoon)[] {
            (MultiStrategyKind.MorningSupportResistanceBounce, MultiStrategyKind.AfternoonSupportResistanceBounce),
            (MultiStrategyKind.MorningSupportResistanceBreakout, MultiStrategyKind.AfternoonSupportResistanceBreakout),
            (MultiStrategyKind.MorningBreakoutRetest, MultiStrategyKind.AfternoonBreakoutRetest),
            (MultiStrategyKind.MorningFibonacciRetracement, MultiStrategyKind.AfternoonFibonacciRetracement),
            (MultiStrategyKind.MorningFibonacciExtension, MultiStrategyKind.AfternoonFibonacciExtension),
            (MultiStrategyKind.MorningLiquiditySweep, MultiStrategyKind.AfternoonLiquiditySweep)
        };

        foreach (var pair in pairs)
        foreach (var kind in new[] { pair.Morning, pair.Afternoon })
        {
            var afternoon = kind == pair.Afternoon;
            var signalIndex = afternoon ? 250 : 75;
            var start = MarketTime.ToUtc(date, new TimeOnly(9, 30));
            var raw = CreateFullSession(date).Select(b =>
            {
                var i = (int)(b.T - start).TotalMinutes;
                if (i < 0 || i >= 390) return b;
                decimal open = 100m, close = 100m, high = 100.25m, low = 99.75m;
                var family = Array.FindIndex(pairs, p =>
                    p.Morning == kind || p.Afternoon == kind);

                if (family == 2)
                {
                    if (i == signalIndex - 5)
                    {
                        open = 100m; close = 101m;
                        high = 101.25m;
                    }
                    if (i > signalIndex - 5 && i < signalIndex)
                    {
                        open = close = 101m;
                        high = 101.25m; low = 100.75m;
                    }
                    if (i == signalIndex)
                    {
                        open = 100.75m; close = 101m;
                        high = 101.25m; low = 100.25m;
                    }
                }
                else if (family == 3 || family == 4)
                {
                    if (i == signalIndex - 25) low = 95m;
                    if (i == signalIndex - 10) high = 105m;
                    if (i == signalIndex)
                    {
                        if (family == 3)
                        {
                            open = 99.5m; close = 100.5m;
                            high = 101m; low = 99.5m;
                        }
                        else
                        {
                            open = 105m; close = 106m;
                            high = 106.25m; low = 104.75m;
                        }
                    }
                }
                else if (i == signalIndex)
                {
                    if (family == 0)
                    {
                        open = 99.75m; close = 100.5m;
                        high = 100.75m;
                    }
                    else if (family == 1)
                    {
                        open = 100m; close = 101m;
                        high = 101.25m;
                    }
                    else if (family == 5)
                    {
                        open = 99.75m; close = 100.5m;
                        high = 100.75m; low = 99m;
                    }
                }
                return b with { C = new Candle {
                    T = b.T, O = open, H = high, L = low,
                    C = close, V = 100
                } };
            }).ToList();

            var session = MultiStrategyData.Build(raw, raw, folder).Single();
            var familyIndex = Array.FindIndex(pairs, p =>
                p.Morning == kind || p.Afternoon == kind);
            var ratio = familyIndex == 4 ? 1.272m : 0.5m;
            var variant = new MultiStrategyVariant(50000 + (int)kind, kind,
                30, 0.25m, 1m, 1.5m, 0m, 0, 0m,
                EconomicEventRule.Off,
                LevelSource: familyIndex is 3 or 4
                    ? PriceLevelSource.Rolling : PriceLevelSource.OpeningRange,
                FibRatio: ratio, RetestBars: 8);
            var signal = PriceActionSignals.Find(variant, session);
            Ensure(signal is { Index: var idx, Side: TradeDirection.Long } &&
                   idx == signalIndex,
                kind + " must produce a causal long signal at fixture bar " +
                signalIndex + " (got " + signal + ").");
            var executed = MultiStrategyEngine.GenerateDay(variant, session);
            Ensure(executed is not null &&
                   executed.EntryAt == session.Rth[signalIndex + 1].T,
                kind + " must enter only at the next bar's open.");
            Ensure(executed!.InitialRiskDollars <= 600m,
                kind + " must respect the bracket risk cap.");

            // A dramatic spike AFTER the chosen signal must not change it.
            var postSignalBar = raw.FindIndex(b =>
                b.T == session.Rth[signalIndex + 25].T);
            var original = raw[postSignalBar];
            var changed = raw.ToList();
            changed[postSignalBar] = original with { C = new Candle {
                T = original.T, O = original.C.O,
                H = original.C.H + 150m, L = original.C.L,
                C = original.C.C, V = original.C.V
            } };
            var future = MultiStrategyData.Build(changed, changed, folder).Single();
            Ensure(Equals(signal, PriceActionSignals.Find(variant, future)),
                kind + " must not use any post-signal high in its entry logic.");
        }

        var first = CreateFullSession(date);
        var nextDay = CreateFullSession(date.AddDays(1));
        var both = first.Concat(nextDay).ToList();
        var past = MultiStrategyData.Build(both, both, folder).Last();
        Ensure(past.PriorCompleteRthDate == date &&
               past.PriorCompleteRthHigh ==
                   first.Where(b => MarketTime.ToEastern(b.T).TimeOfDay >=
                       new TimeSpan(9, 30, 0) &&
                       MarketTime.ToEastern(b.T).TimeOfDay <
                       new TimeSpan(16, 0, 0)).Max(b => b.C.H),
            "Prior completed RTH reference must be based on previous day's actual bars.");

        var rolled = first.Concat(nextDay.Select(b =>
            b with { Contract = "ESZ6" })).ToList();
        var onRollover = MultiStrategyData.Build(rolled, rolled, folder).Last();
        Ensure(onRollover.PriorCompleteRthHigh is null,
            "Support/resistance references must not cross contract rollovers.");

        Console.WriteLine("PASS: 12 price-action family/session signal fixtures, causal Fibonacci targets,");
        Console.WriteLine("      next-open execution, future-bar invariance, and rollover-safe RTH levels.");
    }

    private static void Ensure(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }

    private static List<PriceBar> CreateFullSession(DateOnly day)
    {
        var bars = new List<PriceBar>(1320);
        AddBatches(MarketTime.ToUtc(day.AddDays(-1), new TimeOnly(18, 0)),
            930, 5200m);
        AddBatches(MarketTime.ToUtc(day, new TimeOnly(9, 30)), 390, 5220m);
        return bars.OrderBy(b => b.T).ToList();

        void AddBatches(DateTimeOffset start, int count, decimal anchor)
        {
            for (var i = 0; i < count; i++)
            {
                var time = start.AddMinutes(i);
                var open = anchor + (i % 36) * 0.25m;
                var candle = new Candle {
                    T = time, O = open, H = open + 0.75m, L = open - 0.5m,
                    C = open + (i % 2 == 0 ? 0.25m : -0.25m),
                    V = 100 + (i % 9) * 20
                };
                bars.Add(new PriceBar(time, "ESU6", candle));
            }
        }
    }
}
