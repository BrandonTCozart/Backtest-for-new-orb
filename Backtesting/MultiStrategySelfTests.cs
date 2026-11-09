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
            var missingAudit = File.ReadAllLines(Path.Combine(folder,
                "multi_strategy_data_quality.csv"));
            Ensure(missingAudit[1].Split(',')[4] == "929" &&
                   missingAudit[1].Contains("overnight_first_missing_"),
                   "Overnight audit must count all observed minutes and identify its first gap.");

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

            var variants = MultiStrategyEngine.EnumerateVariants();
            Ensure(variants.Count == variants.Select(x => x.Id).Distinct().Count() &&
                   variants.Count == 416,
                "Candidate grid must contain all 416 unique, reviewed variants.");
            Ensure(Enum.GetValues<MultiStrategyKind>().All(
                k => variants.Any(v => v.Kind == k)),
                "Every intended strategy must have candidate configurations.");

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
            Ensure(MultiStrategyRunner.Run(syntheticYear, syntheticYear, folder) == 0,
                "Complete synthetic multi-strategy backtest should return success.");
            var reportFolder = Path.Combine(folder, "data");
            foreach (var required in new[] {
                "multi_strategy_summary.txt",
                "multi_strategy_data_quality.csv",
                "multi_strategy_full_sample_winners.csv",
                "multi_strategy_full_sample_highest_win_rate.csv",
                "multi_strategy_candidate_scores.csv",
                "multi_strategy_individual_walkforward.csv",
                "multi_strategy_portfolio_walkforward.csv",
                "multi_strategy_portfolio_trades.csv",
                "multi_strategy_portfolio_monthly.csv"
            })
                Ensure(File.Exists(Path.Combine(reportFolder, required)),
                    "Missing completed research report: " + required);

            var oosRows = File.ReadAllLines(Path.Combine(reportFolder,
                    "multi_strategy_portfolio_trades.csv")).Skip(1)
                .Select(x => x.Split(',')).ToArray();
            foreach (var dayGroup in oosRows.GroupBy(x => x[1]))
            {
                var previousExit = DateTimeOffset.MinValue;
                var count = 0;
                foreach (var row in dayGroup.OrderBy(x =>
                    DateTimeOffset.Parse(x[6], System.Globalization.CultureInfo.InvariantCulture)))
                {
                    var entry = DateTimeOffset.Parse(row[6],
                        System.Globalization.CultureInfo.InvariantCulture);
                    var exit = DateTimeOffset.Parse(row[7],
                        System.Globalization.CultureInfo.InvariantCulture);
                    Ensure(entry >= previousExit && exit > entry,
                        "Portfolio must never overlap two positions.");
                    previousExit = exit;
                    count++;
                }
                Ensure(count <= 3, "Maximum three sequential trades per trading date.");
            }

            Console.WriteLine("PASS: full 25-session synthetic walk-forward and all report outputs.");
            Console.WriteLine("PASS: synthetic full/missing RTH and overnight minute coverage,");
            Console.WriteLine("      lookahead boundary checks, trade risk/close-time invariants,");
            Console.WriteLine("      seven strategy candidate families, P&L and event-calendar checks.");
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
