using System.Globalization;

namespace Backtest_for_new_orb;

public static class OptimizationRunner
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    public static int Run()
    {
        Console.WriteLine("ES ORB: bounded exhaustive configuration optimizer");
        Console.WriteLine("No download. No Topstep connection. No live trades.");
        Console.WriteLine("Original Orb stop stages (NOT $500/$450 profit lock).");
        Console.WriteLine("Limits: max 2 ES first entry; optional 0, 5, 10 or 20 MES Stage 6.");
        Console.WriteLine("IMPORTANT: MES P&L uses ES candles as a proxy, not actual MES history.");

        var folder = Path.Combine(BacktestRunner.FindRoot(), "data");
        var files = new[] { "es_1m_3months.csv", "es_1m_3months_INCOMPLETE.csv" }
            .Select(n => Path.Combine(folder, n)).Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine("No candles. Expected es_1m_3months.csv or es_1m_3months_INCOMPLETE.csv in " + folder);
            return 1;
        }
        var input = files[0];
        if (Path.GetFileName(input).Contains("INCOMPLETE", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("WARNING: Input download is incomplete; missing entire dates cannot be evaluated.");
        Console.WriteLine("Input: " + input);

        var candles = BacktestRunner.Load(input);
        if (candles.Count == 0) return 1;
        var sessions = OptimizationData.Prepare(candles)
            .Where(s => s.Range is not null)
            .OrderBy(s => s.Day).ToArray();
        if (sessions.Length < 10)
        {
            Console.Error.WriteLine("Need at least 10 sessions with a complete 9:30–9:45 ET opening range for a train/holdout split; found " + sessions.Length);
            return 2;
        }

        // Holdout is chosen BEFORE examining any strategy P&L, and never used
        // for ranking or selecting the optimal parameters.
        var trainingCount = Math.Max(1, (int)Math.Floor(sessions.Length * 0.70m));
        var train = sessions.Take(trainingCount).ToArray();
        var holdout = sessions.Skip(trainingCount).ToArray();
        var minFirstTrades = Math.Max(4, (train.Length + 3) / 4);

        Console.WriteLine("Complete opening-range sessions: " + sessions.Length);
        Console.WriteLine("Training: " + train.Length + " (" + train[0].Day + " to " + train[^1].Day + ")");
        Console.WriteLine("Holdout: " + holdout.Length + " (" + holdout[0].Day + " to " + holdout[^1].Day + ")");
        Console.WriteLine("Minimum first ES trades to qualify: " + minFirstTrades);
        Console.WriteLine("Testing every bounded grid combination recursively. Results are approximate.");

        Directory.CreateDirectory(folder);
        var configsFile = Path.Combine(folder, "optimizer_all_configurations.csv");
        var holdoutFile = Path.Combine(folder, "optimizer_top10_holdout.csv");
        var tradesFile = Path.Combine(folder, "optimizer_best_trades.csv");
        var monthlyFile = Path.Combine(folder, "optimizer_best_monthly_pnl.csv");
        var summaryFile = Path.Combine(folder, "optimizer_best_configuration.txt");

        const int SubsetsPerBase = WeekdaySelection.All;
        const int BaseCount = 20736;
        Console.WriteLine("Weekday masks: all 31 nonempty subsets of Monday–Friday.");
        Console.WriteLine("Expected combinations: " +
            (BaseCount * SubsetsPerBase).ToString("N0", CI) + " (20,736 × 31)");
        Console.WriteLine("Trade simulation is reused across 31 weekday subsets.");
        Console.WriteLine("All-configuration CSV is written incrementally to limit memory use.");

        var topTen = new List<OptimizationResult>(10);
        var qualified = 0;
        var total = 0;
        using (var writer = new StreamWriter(configsFile, false))
        {
            writer.WriteLine("config_id,entry_cutoff_et,weekday_mask,weekdays,orb_min_points,orb_max_points,vwap_rule,breakout_buffer_points,volume_multiplier,retest_required,es_contracts,max_es_initial_risk,es_fallback_stop_risk,stage5_es_trail_dollars,stage6_mes_contracts,training_net_pnl,training_gross_pnl,training_costs,training_max_closed_drawdown,training_es_trades,training_mes_trades,training_wins,training_skipped_days,qualifies");
            foreach (var baseConfig in EnumerateConfigurations())
            {
                foreach (var score in OptimizationEngine.EvaluateWeekdayVariants(baseConfig, train))
                {
                    total++;
                    var qualifies = score.EsTrades >= minFirstTrades;
                    if (qualifies)
                    {
                        qualified++;
                        topTen.Add(score);
                        topTen = topTen.OrderByDescending(x => x.Net)
                            .ThenBy(x => x.MaxDrawdown)
                            .ThenByDescending(x => x.EsTrades)
                            .ThenBy(x => x.Config.Id)
                            .Take(10).ToList();
                    }
                    writer.WriteLine(score.Config.Id.ToString(CI) + "," +
                        Params(score.Config) + "," +
                        string.Join(",", F(score.Net), F(score.Gross), F(score.Costs),
                            F(score.MaxDrawdown), score.EsTrades, score.MesTrades,
                            score.WinningTrades, score.SkippedDays,
                            qualifies ? "yes" : "no"));
                }
                if (baseConfig.Id % 100 == 0)
                {
                    writer.Flush();
                    Console.WriteLine("Base configurations " +
                        baseConfig.Id.ToString("N0", CI) + "/" +
                        BaseCount.ToString("N0", CI) + "; weekday variants " +
                        total.ToString("N0", CI) + "/" +
                        (BaseCount * SubsetsPerBase).ToString("N0", CI));
                }
            }
        }

        if (topTen.Count == 0)
        {
            Console.Error.WriteLine("No configurations met the minimum ES trade count.");
            Console.Error.WriteLine("Partial/all tested configurations: " + configsFile);
            return 2;
        }

        var top = topTen.ToArray();
        var testRows = new List<string> {
            "training_rank,config_id,training_net_pnl,holdout_net_pnl,holdout_es_trades,holdout_mes_trades,holdout_wins,holdout_max_closed_drawdown,entry_cutoff_et,weekday_mask,weekdays,orb_min_points,orb_max_points,vwap_rule,breakout_buffer_points,volume_multiplier,retest_required,es_contracts,max_es_initial_risk,es_fallback_stop_risk,stage5_es_trail_dollars,stage6_mes_contracts"
        };
        Console.WriteLine();
        Console.WriteLine("TRAINING RANKING — top ten configurations");
        for (var i = 0; i < top.Length; i++)
        {
            var score = top[i];
            var test = OptimizationEngine.Evaluate(score.Config, holdout);
            testRows.Add(string.Join(",",
                (i + 1).ToString(CI), score.Config.Id.ToString(CI),
                F(score.Net), F(test.Net), test.EsTrades.ToString(CI),
                test.MesTrades.ToString(CI), test.WinningTrades.ToString(CI),
                F(test.MaxDrawdown), Params(score.Config)));
            Console.WriteLine("Rank " + (i + 1) + ", config " + score.Config.Id +
                ": training net $" + F(score.Net) +
                ", untouched holdout net $" + F(test.Net) +
                ", holdout first trades " + test.EsTrades);
        }
        File.WriteAllLines(holdoutFile, testRows);

        var best = top[0];
        var untouched = OptimizationEngine.Evaluate(best.Config, holdout);
        var full = OptimizationEngine.Evaluate(best.Config, sessions, keepTrades: true);
        var sequence = full.DetailedTrades ?? new List<SimulatedTrade>();
        decimal cumulative = 0m;
        var tradeRows = new List<string> {
            "date_et,instrument,contract,quantity,direction,entry_time_et,entry_price,exit_time_et,exit_price,exit_reason,mfe_points,gross_pnl_dollars,estimated_cost_dollars,net_pnl_dollars,cumulative_net_pnl_dollars,split"
        };
        foreach (var t in sequence)
        {
            cumulative += t.Net;
            tradeRows.Add(string.Join(",",
                t.Date.ToString("yyyy-MM-dd", CI),
                t.IsMes ? "MES_ES_PRICE_PROXY" : "ES",
                t.Contract, t.Quantity.ToString(CI), t.Direction,
                Et(t.EntryAt), F(t.Entry), Et(t.ExitAt), F(t.Exit),
                t.ExitReason, F(t.MfePoints), F(t.Gross), F(t.EstimatedCosts),
                F(t.Net), F(cumulative), t.Date <= train[^1].Day ? "train" : "holdout"));
        }
        File.WriteAllLines(tradesFile, tradeRows);

        decimal running = 0m;
        var monthRows = new List<string> { "month_et,trades,es_trades,mes_proxy_trades,gross_pnl,costs,net_pnl,cumulative_net_pnl" };
        foreach (var g in sequence.GroupBy(t => (t.Date.Year, t.Date.Month))
                     .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month))
        {
            var group = g.ToArray();
            var gross = group.Sum(t => t.Gross);
            var costs = group.Sum(t => t.EstimatedCosts);
            var net = gross - costs;
            running += net;
            monthRows.Add(string.Join(",",
                g.Key.Year.ToString("D4", CI) + "-" + g.Key.Month.ToString("D2", CI),
                group.Length, group.Count(t => !t.IsMes), group.Count(t => t.IsMes),
                F(gross), F(costs), F(net), F(running)));
        }
        File.WriteAllLines(monthlyFile, monthRows);

        var winner = new[] {
            "BEST BOUNDED CONFIGURATION — selected exclusively on training net P&L",
            "ID: " + best.Config.Id,
            "Entry cutoff (ET): " + best.Config.Cutoff.ToString(@"hh\:mm"),
            "Allowed trading weekdays (ET): " + WeekdaySelection.Format(best.Config.WeekdayMask),
            "Opening range width: " + best.Config.Range.Min + "–" + best.Config.Range.Max + " ES points",
            "VWAP rule: " + best.Config.Vwap,
            "Breakout buffer: " + best.Config.BreakoutPoints + " points",
            "Breakout volume threshold: " + (best.Config.VolumeMultiplier == 0 ? "off" : best.Config.VolumeMultiplier + "x previous-ten-minute average"),
            "Retest within next 5 bars: " + best.Config.Retest,
            "First position: " + best.Config.EsQuantity + " ES",
            "Initial risk cap / fallback: $" + best.Config.Risk.MaxRisk + " / $" + best.Config.Risk.FallbackRisk + " (total trade)",
            "Stage-5 trail: $" + best.Config.EsTrailDollars + " (original $1000 activation)",
            "Optional Stage-6 position: " + best.Config.MesQuantity + " MES (ES-derived price proxy)",
            "TRAINING net P&L: $" + F(best.Net) + ", ES trades: " + best.EsTrades + ", closed-trade max drawdown: $" + F(best.MaxDrawdown),
            "UNTOUCHED HOLDOUT net P&L: $" + F(untouched.Net) + ", ES trades: " + untouched.EsTrades + ", closed-trade max drawdown: $" + F(untouched.MaxDrawdown),
            "FULL SAMPLE net P&L (descriptive only): $" + F(full.Net) + ", ES trades: " + full.EsTrades + ", MES proxy trades: " + full.MesTrades,
            "Configurations evaluated: " + total + "; qualified: " + qualified,
            "WARNING: Winner is optimal ONLY within this finite grid on the TRAINING sample.",
            "WARNING: MES results use ES prices; tick-level ordering, fills and actual MES prices are unknown.",
            "WARNING: The CSV sample is short; searching thousands of candidates can overfit it.",
            "Never treat this as evidence of future returns or automatically deploy it live."
        };
        File.WriteAllLines(summaryFile, winner);
        Console.WriteLine();
        Console.WriteLine("CONFIGURATIONS TESTED: " + total.ToString("N0", CI));
        Console.WriteLine("TOP TRAINING CONFIGURATION: " + best.Config.Id);
        foreach (var line in winner.Skip(2).Take(14)) Console.WriteLine(line);
        Console.WriteLine("All grid results: " + configsFile);
        Console.WriteLine("Top ten holdout check: " + holdoutFile);
        Console.WriteLine("Best trade detail: " + tradesFile);
        Console.WriteLine("Best monthly P&L: " + monthlyFile);
        Console.WriteLine("Winner parameters: " + summaryFile);
        return 0;
    }

    // True recursive Cartesian-product enumeration. Every enumerated configuration
    // is evaluated, rather than randomly sampled or guessed.
    internal static IEnumerable<OptimizationConfig> EnumerateConfigurations()
    {
        object[][] axes = {
            new object[] { new TimeSpan(10,0,0), new TimeSpan(10,30,0), new TimeSpan(11,0,0), new TimeSpan(16,0,0) },
            new object[] { new RangeBand(0m,10000m), new RangeBand(6m,25m), new RangeBand(10m,40m) },
            new object[] { VwapRule.Off, VwapRule.Direction, VwapRule.DirectionAndSlope },
            new object[] { 0m, .5m, 1m },
            new object[] { 0m, 1.25m },
            new object[] { false, true },
            new object[] { 1, 2 },
            new object[] { new RiskBand(600m,500m), new RiskBand(500m,400m) },
            new object[] { 150m, 250m, 350m },
            new object[] { 0, 5, 10, 20 }
        };
        var chosen = new object[axes.Length];
        int id = 0;
        return Traverse(0);

        IEnumerable<OptimizationConfig> Traverse(int dimension)
        {
            if (dimension == axes.Length)
            {
                yield return new OptimizationConfig(++id, (TimeSpan)chosen[0],
                    (RangeBand)chosen[1], (VwapRule)chosen[2], (decimal)chosen[3],
                    (decimal)chosen[4], (bool)chosen[5], (int)chosen[6],
                    (RiskBand)chosen[7], (decimal)chosen[8], (int)chosen[9]);
                yield break;
            }
            foreach (var value in axes[dimension])
            {
                chosen[dimension] = value;
                foreach (var config in Traverse(dimension + 1))
                    yield return config;
            }
        }
    }

    internal static string Params(OptimizationConfig c) => string.Join(",",
        c.Cutoff.ToString(@"hh\:mm"),
        c.WeekdayMask.ToString(CI), WeekdaySelection.Format(c.WeekdayMask),
        F(c.Range.Min), F(c.Range.Max), c.Vwap, F(c.BreakoutPoints),
        F(c.VolumeMultiplier), c.Retest ? "yes" : "no",
        c.EsQuantity.ToString(CI), F(c.Risk.MaxRisk),
        F(c.Risk.FallbackRisk), F(c.EsTrailDollars),
        c.MesQuantity.ToString(CI));
    private static string F(decimal value) => value.ToString("0.00", CI);
    private static string Et(DateTimeOffset time) => Orb.MarketTime.ToEastern(time)
        .ToString("yyyy-MM-ddTHH:mm:sszzz", CI);
}
