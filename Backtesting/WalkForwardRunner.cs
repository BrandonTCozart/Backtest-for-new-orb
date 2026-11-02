using System.Globalization;

namespace Backtest_for_new_orb;

/// <summary>
/// Expanding-window walk-forward evaluation: optimize ONLY on prior days,
/// execute the selected strategy on the immediately following unseen days.
/// Test blocks never overlap. Test results never influence selecting a winner.
/// </summary>
public static class WalkForwardRunner
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    private const int BaseCombinationCount = 20736;
    private const int WeekdaySubsetCount = WeekdaySelection.All;
    private static readonly int EconomicRuleCount = Enum.GetValues<EconomicEventRule>().Length;

    public static int Run()
    {
        Console.WriteLine("ORB FULL-DATASET OPTIMIZER + WALK-FORWARD TEST");
        Console.WriteLine("Using original Orb stage stops, not the $500/$450 experiment.");
        Console.WriteLine("Only local ES candles; no Topstep access or live orders.");
        Console.WriteLine("Caps: first position 1-2 ES; optional reversal 0-20 MES (ES price proxy).");

        var directory = Path.Combine(BacktestRunner.FindRoot(), "data");
        var files = new[] { TopstepHistoryDownloader.OutputName, "es_1m_3months.csv", "es_1m_3months_INCOMPLETE.csv" }
            .Select(filename => Path.Combine(directory, filename))
            .Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine("No ES CSV found in " + directory);
            return 1;
        }
        var csvPath = files[0];
        if (Path.GetFileName(csvPath).Contains("INCOMPLETE", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("Input file: INCOMPLETE historical candle export.");
        Console.WriteLine("Reading " + csvPath);

        var bars = BacktestRunner.Load(csvPath);
        var sessions = OptimizationData.Prepare(bars)
            .Where(s => s.Range is not null)
            .OrderBy(s => s.Day).ToArray();
        if (sessions.Length < 20)
        {
            Console.Error.WriteLine("Walk-forward needs >=20 sessions with complete 9:30-9:45 opening ranges. Found " + sessions.Length);
            return 2;
        }

        var folds = WalkForwardFold.Create(sessions.Length);
        Console.WriteLine("Complete opening-range sessions: " + sessions.Length);
        foreach (var fold in folds)
        {
            Console.WriteLine("Fold " + fold.Number +
                ": TRAIN " + sessions[fold.TrainingStart].Day +
                " to " + sessions[fold.TrainingEnd - 1].Day +
                " (" + fold.TrainingSessions + " days); TEST " +
                sessions[fold.TestStart].Day + " to " +
                sessions[fold.TestEnd - 1].Day + " (" + fold.TestSessions +
                " days); minimum training ES trades " + fold.MinimumTrainingTrades);
        }
        Console.WriteLine("Evaluating " +
            (BaseCombinationCount * WeekdaySubsetCount * EconomicRuleCount).ToString("N0", CI) +
            " configurations on EACH of 3 training windows AND the full dataset.");
        Console.WriteLine("Daily trade paths are reused across weekdays, event policies and folds.");
        Console.WriteLine("Event policies: Off; skip major release days; skip 30m before/15m after intraday releases; skip FOMC decision day and prior trading day.");
        Console.WriteLine("No future test-period P&L is used to select configurations.");

        Directory.CreateDirectory(directory);
        var gridPath = Path.Combine(directory, "optimizer_walk_forward_all_configurations.csv");
        var foldsPath = Path.Combine(directory, "optimizer_walk_forward_folds.csv");
        var tradesPath = Path.Combine(directory, "optimizer_walk_forward_trades.csv");
        var monthsPath = Path.Combine(directory, "optimizer_walk_forward_monthly_pnl.csv");
        var summaryPath = Path.Combine(directory, "optimizer_walk_forward_summary.txt");
        var fullComparisonPath = Path.Combine(directory, "optimizer_full_sample_comparison.csv");
        var latestFullTradesPath = Path.Combine(directory, "optimizer_full_sample_latest_config_trades.csv");
        var optimalFullTradesPath = Path.Combine(directory, "optimizer_full_dataset_best_trades.csv");
        var optimalFullSummaryPath = Path.Combine(directory, "optimizer_full_dataset_best_summary.txt");
        EconomicCalendar.WriteCalendarCsv(directory);
        Console.WriteLine("Economic release schedule: " + Path.Combine(directory, "optimizer_economic_calendar_2026.csv"));

        var winningTraining = new OptimizationResult?[folds.Length];
        var eligibleCount = new long[folds.Length];
        var minimumFullSampleTrades = Math.Max(4, (sessions.Length + 3) / 4);
        OptimizationResult? fullDatasetWinner = null;
        long eligibleFullCount = 0;
        long evaluated = 0;
        int baseNumber = 0;

        using (var writer = new StreamWriter(gridPath, false))
        {
            writer.WriteLine("config_id,entry_cutoff_et,weekday_mask,weekdays,orb_min_points,orb_max_points,vwap_rule,breakout_buffer_points,volume_multiplier,retest_required,es_contracts,max_es_initial_risk,es_fallback_stop_risk,stage5_es_trail_dollars,stage6_mes_contracts,economic_event_rule," +
                string.Join(",", folds.Select(f => "fold" + f.Number + "_training_net_pnl,fold" +
                    f.Number + "_training_es_trades,fold" + f.Number +
                    "_training_max_drawdown,fold" + f.Number + "_qualifies")) +
                ",full_sample_net_pnl,full_sample_gross_pnl,full_sample_costs,full_sample_es_trades,full_sample_mes_proxy_trades,full_sample_wins,full_sample_max_drawdown,full_sample_qualifies");

            foreach (var baseConfig in OptimizationRunner.EnumerateConfigurations())
            {
                baseNumber++;
                var scoresByFold = new OptimizationResult[folds.Length];
                // The engine reuses each candidate's simulated daily trades for
                // three training folds and a separate full-dataset ranking.
                foreach (var (foldIndex, score) in
                         OptimizationEngine.EvaluateWalkForwardTrainingVariants(
                             baseConfig, sessions, folds))
                {
                    if (foldIndex < folds.Length)
                    {
                        scoresByFold[foldIndex] = score;
                        var eligibleTraining = score.EsTrades >=
                            folds[foldIndex].MinimumTrainingTrades;
                        if (eligibleTraining)
                        {
                            eligibleCount[foldIndex]++;
                            if (IsBetter(score, winningTraining[foldIndex]))
                                winningTraining[foldIndex] = score;
                        }
                    }

                    // Full-sample score arrives after the three fold scores
                    // for the same candidate. It is used ONLY for selecting
                    // the fixed best-overall configuration.
                    if (foldIndex == folds.Length)
                    {
                        bool qualifiesFull = score.EsTrades >= minimumFullSampleTrades;
                        if (qualifiesFull)
                        {
                            eligibleFullCount++;
                            if (IsBetter(score, fullDatasetWinner))
                                fullDatasetWinner = score;
                        }
                        var firstScore = scoresByFold[0];
                        writer.Write(firstScore.Config.Id.ToString(CI));
                        writer.Write(',');
                        writer.Write(OptimizationRunner.Params(firstScore.Config));
                        for (var i = 0; i < folds.Length; i++)
                        {
                            var item = scoresByFold[i];
                            writer.Write(',');
                            writer.Write(F(item.Net));
                            writer.Write(',');
                            writer.Write(item.EsTrades.ToString(CI));
                            writer.Write(',');
                            writer.Write(F(item.MaxDrawdown));
                            writer.Write(',');
                            writer.Write(item.EsTrades >= folds[i].MinimumTrainingTrades ? "yes" : "no");
                        }
                        writer.Write(',');
                        writer.Write(F(score.Net));
                        writer.Write(',');
                        writer.Write(F(score.Gross));
                        writer.Write(',');
                        writer.Write(F(score.Costs));
                        writer.Write(',');
                        writer.Write(score.EsTrades.ToString(CI));
                        writer.Write(',');
                        writer.Write(score.MesTrades.ToString(CI));
                        writer.Write(',');
                        writer.Write(score.WinningTrades.ToString(CI));
                        writer.Write(',');
                        writer.Write(F(score.MaxDrawdown));
                        writer.Write(',');
                        writer.Write(qualifiesFull ? "yes" : "no");
                        writer.WriteLine();
                        evaluated++;
                    }
                }

                if (baseNumber % 100 == 0)
                {
                    writer.Flush();
                    Console.WriteLine("Base configurations " + baseNumber.ToString("N0", CI) +
                        "/" + BaseCombinationCount.ToString("N0", CI) +
                        "; variants scored per fold " + evaluated.ToString("N0", CI) +
                        "/" + (BaseCombinationCount * WeekdaySubsetCount * EconomicRuleCount).ToString("N0", CI));
                }
            }
        }

        if (winningTraining.Any(w => w is null) || fullDatasetWinner is null)
        {
            Console.Error.WriteLine("No configurations qualified in at least one selection window.");
            Console.Error.WriteLine("Saved all evaluated configurations: " + gridPath);
            return 2;
        }

        // Evaluate winners on held-out NEXT blocks only AFTER ALL folds' training
        // winners have been chosen. These out-of-sample results select nothing.
        var combinedOutOfSampleTrades = new List<(int FoldNumber, SimulatedTrade Trade)>();
        var testNetByFold = new decimal[folds.Length];
        var fullSampleResults = new OptimizationResult[folds.Length];
        var fullSampleRows = new List<string> {
            "selected_in_fold,config_id,sample_start_et,sample_end_et,opening_range_sessions,full_sample_net_pnl,full_sample_gross_pnl,full_sample_costs,full_sample_es_trades,full_sample_mes_proxy_trades,full_sample_wins,full_sample_max_closed_drawdown,selected_fold_test_net_pnl,selected_fold_test_es_trades,entry_cutoff_et,weekday_mask,weekdays,orb_min_points,orb_max_points,vwap_rule,breakout_buffer_points,volume_multiplier,retest_required,es_contracts,max_es_initial_risk,es_fallback_stop_risk,stage5_es_trail_dollars,stage6_mes_contracts,economic_event_rule"
        };
        var foldReports = new List<string> {
            "fold_number,training_start_et,training_end_et,training_sessions,minimum_training_es_trades,test_start_et,test_end_et,test_sessions,selected_config_id,training_net_pnl,training_es_trades,training_mes_proxy_trades,training_max_drawdown,test_net_pnl,test_gross_pnl,test_estimated_costs,test_es_trades,test_mes_proxy_trades,test_winning_trades,test_max_closed_drawdown,entry_cutoff_et,weekday_mask,weekdays,orb_min_points,orb_max_points,vwap_rule,breakout_buffer_points,volume_multiplier,retest_required,es_contracts,max_es_initial_risk,es_fallback_stop_risk,stage5_es_trail_dollars,stage6_mes_contracts,economic_event_rule,full_sample_net_pnl,full_sample_es_trades,full_sample_mes_proxy_trades,full_sample_wins,full_sample_max_closed_drawdown"
        };
        var summary = new List<string> {
            "ORB FULL-DATASET OPTIMIZER + WALK-FORWARD RESULTS",
            "Dataset: " + Path.GetFileName(csvPath),
            "Full eligible session range: " + sessions[0].Day + " through " + sessions[^1].Day,
            "Method: expanding-window train, select on training P&L only, test immediately following dates",
            "Config grid: " + evaluated.ToString("N0", CI) + " distinct configurations, trained 3 times each",
            "Economic rules: Off / AvoidMajorReleaseDay / AvoidIntradayReleaseWindow / AvoidFomcDayAndPriorTradingDay",
            "Full-grid best-overall selection and three fold-training winners are evaluated separately.",
            ""
        };

        Console.WriteLine();
        Console.WriteLine("WALK-FORWARD RESULTS (genuinely out-of-sample blocks)");
        for (var i = 0; i < folds.Length; i++)
        {
            var fold = folds[i];
            var chosen = winningTraining[i]!;
            var testingSessions = sessions
                .Skip(fold.TestStart).Take(fold.TestSessions).ToArray();
            var test = OptimizationEngine.Evaluate(chosen.Config,
                testingSessions, keepTrades: true);
            testNetByFold[i] = test.Net;

            // Full-sample replay of THIS fold's selected configuration, including
            // earlier training dates. Separate from the stitched walk-forward test.
            var fullSample = OptimizationEngine.Evaluate(chosen.Config, sessions,
                keepTrades: i == folds.Length - 1);
            fullSampleResults[i] = fullSample;
            fullSampleRows.Add(string.Join(",",
                fold.Number.ToString(CI), chosen.Config.Id.ToString(CI),
                Day(sessions[0].Day), Day(sessions[^1].Day),
                sessions.Length.ToString(CI),
                F(fullSample.Net), F(fullSample.Gross), F(fullSample.Costs),
                fullSample.EsTrades.ToString(CI), fullSample.MesTrades.ToString(CI),
                fullSample.WinningTrades.ToString(CI), F(fullSample.MaxDrawdown),
                F(test.Net), test.EsTrades.ToString(CI),
                OptimizationRunner.Params(chosen.Config)));

            foreach (var trade in test.DetailedTrades ?? [])
                combinedOutOfSampleTrades.Add((fold.Number, trade));

            foldReports.Add(string.Join(",",
                fold.Number.ToString(CI),
                Day(sessions[fold.TrainingStart].Day),
                Day(sessions[fold.TrainingEnd - 1].Day),
                fold.TrainingSessions.ToString(CI),
                fold.MinimumTrainingTrades.ToString(CI),
                Day(sessions[fold.TestStart].Day),
                Day(sessions[fold.TestEnd - 1].Day),
                fold.TestSessions.ToString(CI),
                chosen.Config.Id.ToString(CI),
                F(chosen.Net), chosen.EsTrades.ToString(CI),
                chosen.MesTrades.ToString(CI), F(chosen.MaxDrawdown),
                F(test.Net), F(test.Gross), F(test.Costs),
                test.EsTrades.ToString(CI), test.MesTrades.ToString(CI),
                test.WinningTrades.ToString(CI), F(test.MaxDrawdown),
                OptimizationRunner.Params(chosen.Config),
                F(fullSample.Net), fullSample.EsTrades.ToString(CI),
                fullSample.MesTrades.ToString(CI),
                fullSample.WinningTrades.ToString(CI),
                F(fullSample.MaxDrawdown)));
            var line = "Fold " + fold.Number + ": training $" + F(chosen.Net) +
                " (" + chosen.EsTrades + " ES trades)" +
                " -> NEXT-BLOCK TEST $" + F(test.Net) +
                " (" + test.EsTrades + " ES trades, " + test.MesTrades +
                " MES proxy trades), config " + chosen.Config.Id +
                ", days " + WeekdaySelection.Format(chosen.Config.WeekdayMask) +
                ", events " + chosen.Config.EventRule;
            Console.WriteLine(line);
            summary.Add(line);
            var fullLine = "  SAME CONFIG ON FULL DATASET: net $" +
                F(fullSample.Net) + ", ES trades " + fullSample.EsTrades +
                ", MES proxy trades " + fullSample.MesTrades +
                ", wins " + fullSample.WinningTrades +
                ", max closed-trade drawdown $" + F(fullSample.MaxDrawdown);
            Console.WriteLine(fullLine);
            summary.Add(fullLine);
            summary.Add("  Test dates: " + sessions[fold.TestStart].Day +
                " to " + sessions[fold.TestEnd - 1].Day);
            summary.Add("  Qualifying training configurations: " +
                eligibleCount[i].ToString("N0", CI));
            summary.Add("  Selected settings: " +
                HumanSettings(chosen.Config));
            summary.Add("");
        }
        File.WriteAllLines(foldsPath, foldReports);
        File.WriteAllLines(fullComparisonPath, fullSampleRows);

        var sequenced = combinedOutOfSampleTrades
            .OrderBy(x => x.Trade.EntryAt).ToArray();
        decimal runningNet = 0m, peakNet = 0m, maxDrawdown = 0m;
        var tradeLines = new List<string> {
            "fold_number,date_et,instrument,source_contract,quantity,direction,entry_time_et,entry_price,exit_time_et,exit_price,exit_reason,mfe_points,gross_pnl_dollars,estimated_cost_dollars,net_pnl_dollars,cumulative_oos_net_pnl_dollars"
        };
        foreach (var row in sequenced)
        {
            var trade = row.Trade;
            runningNet += trade.Net;
            peakNet = Math.Max(peakNet, runningNet);
            maxDrawdown = Math.Max(maxDrawdown, peakNet - runningNet);
            tradeLines.Add(string.Join(",",
                row.FoldNumber.ToString(CI), Day(trade.Date),
                trade.IsMes ? "MES_ES_PRICE_PROXY" : "ES",
                trade.Contract, trade.Quantity.ToString(CI), trade.Direction,
                Et(trade.EntryAt), F(trade.Entry), Et(trade.ExitAt), F(trade.Exit),
                trade.ExitReason, F(trade.MfePoints), F(trade.Gross),
                F(trade.EstimatedCosts), F(trade.Net), F(runningNet)));
        }
        File.WriteAllLines(tradesPath, tradeLines);

        decimal monthlyRunning = 0m;
        var monthlyLines = new List<string> {
            "month_et,es_trades,mes_proxy_trades,gross_pnl_dollars,estimated_cost_dollars,net_pnl_dollars,cumulative_oos_net_pnl_dollars"
        };
        foreach (var group in sequenced.GroupBy(x => (x.Trade.Date.Year, x.Trade.Date.Month))
                     .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month))
        {
            var trades = group.Select(x => x.Trade).ToArray();
            var gross = trades.Sum(t => t.Gross);
            var costs = trades.Sum(t => t.EstimatedCosts);
            var net = gross - costs;
            monthlyRunning += net;
            monthlyLines.Add(string.Join(",",
                group.Key.Year.ToString("D4", CI) + "-" + group.Key.Month.ToString("D2", CI),
                trades.Count(t => !t.IsMes), trades.Count(t => t.IsMes),
                F(gross), F(costs), F(net), F(monthlyRunning)));
        }
        File.WriteAllLines(monthsPath, monthlyLines);

        var allGross = sequenced.Sum(x => x.Trade.Gross);
        var allCosts = sequenced.Sum(x => x.Trade.EstimatedCosts);
        var won = sequenced.Count(x => x.Trade.Net > 0);
        var lost = sequenced.Count(x => x.Trade.Net < 0);
        var positiveFolds = testNetByFold.Count(net => net > 0m);

        Console.WriteLine();
        Console.WriteLine("TOTAL WALK-FORWARD OUT-OF-SAMPLE NET P&L: $" + F(runningNet));
        Console.WriteLine("Gross: $" + F(allGross) + "; estimated fees/slippage: $" + F(allCosts));
        Console.WriteLine("ES trades: " + sequenced.Count(x => !x.Trade.IsMes) +
            "; MES ES-price-proxy trades: " + sequenced.Count(x => x.Trade.IsMes));
        Console.WriteLine("Winning trades: " + won + "; losing trades: " + lost);
        Console.WriteLine("Max closed-trade cumulative OOS drawdown: $" + F(maxDrawdown));
        Console.WriteLine("Profitable walk-forward test blocks: " +
            positiveFolds + "/" + folds.Length);

        var latestConfig = winningTraining[^1]!.Config;
        var latestFull = fullSampleResults[^1];
        var fullBest = OptimizationEngine.Evaluate(fullDatasetWinner!.Config,
            sessions, keepTrades: true);

        Console.WriteLine();
        Console.WriteLine("BEST OVERALL CONFIGURATION (optimized across ALL " +
            sessions.Length + " opening-range sessions)");
        Console.WriteLine("Selected from " + evaluated.ToString("N0", CI) +
            " configurations; " + eligibleFullCount.ToString("N0", CI) +
            " met the minimum " + minimumFullSampleTrades + " ES trades.");
        Console.WriteLine("Config " + fullBest.Config.Id +
            ": FULL DATASET NET P&L $" + F(fullBest.Net) +
            "; gross $" + F(fullBest.Gross) +
            "; costs $" + F(fullBest.Costs));
        Console.WriteLine("Trades: " + fullBest.EsTrades + " ES, " +
            fullBest.MesTrades + " MES proxy; wins " +
            fullBest.WinningTrades + "; maximum closed-trade drawdown $" +
            F(fullBest.MaxDrawdown));
        foreach (var line in HumanSettings(fullBest.Config).Split("; "))
            Console.WriteLine(line);

        Console.WriteLine();
        Console.WriteLine("FULL-DATASET RESULTS (one fixed configuration per row)");
        Console.WriteLine("All " + sessions.Length +
            " complete opening-range sessions; each fold's chosen config replayed from first through last date.");
        for (var i = 0; i < folds.Length; i++)
        {
            var result = fullSampleResults[i];
            Console.WriteLine("Fold " + folds[i].Number + " config " + result.Config.Id +
                ": FULL net $" + F(result.Net) +
                ", " + result.EsTrades + " ES trades, " +
                result.MesTrades + " MES proxy trades; corresponding test-block net $" +
                F(testNetByFold[i]));
        }
        Console.WriteLine("WALK-FORWARD OOS TOTAL (different selected configs in different folds): $" +
            F(runningNet));
        Console.WriteLine("BEST OVERALL ACROSS FULL GRID: config " + fullBest.Config.Id +
            ", net $" + F(fullBest.Net) +
            ", " + fullBest.EsTrades + " ES trades");
        Console.WriteLine("LATEST FOLD-3 TRAINING WINNER ON FULL DATASET: config " +
            latestConfig.Id + ", net $" + F(latestFull.Net) +
            " (" + latestFull.EsTrades + " ES trades, " +
            latestFull.MesTrades + " MES proxy trades)");
        Console.WriteLine("Full-dataset results include the training dates; walk-forward OOS excludes those dates.");

        Console.WriteLine();
        Console.WriteLine("MOST RECENT TRAINING-WINDOW WINNER: config " + latestConfig.Id);
        foreach (var entry in HumanSettings(latestConfig).Split("; "))
            Console.WriteLine(entry);

        summary.Add("BEST OVERALL CONFIGURATION — selected on all " +
            sessions.Length + " available opening-range sessions");
        summary.Add("Full-dataset grid winner: config " + fullBest.Config.Id);
        summary.Add("FULL DATASET NET P&L: $" + F(fullBest.Net) +
            "; gross $" + F(fullBest.Gross) + "; costs $" + F(fullBest.Costs));
        summary.Add("Trades: " + fullBest.EsTrades + " ES, " + fullBest.MesTrades +
            " MES proxy; wins " + fullBest.WinningTrades +
            "; max closed-trade drawdown $" + F(fullBest.MaxDrawdown));
        summary.Add("Minimum eligible ES trades: " + minimumFullSampleTrades +
            "; qualifying full-dataset configurations: " + eligibleFullCount);
        summary.Add(HumanSettings(fullBest.Config));
        summary.Add("");
        summary.Add("COMBINED OUT-OF-SAMPLE NET P&L (stitched fold test blocks): $" + F(runningNet));
        summary.Add("FULL-DATASET RESULTS (separate fixed-config replays):");
        for (var i = 0; i < folds.Length; i++)
        {
            var full = fullSampleResults[i];
            summary.Add("  Fold " + folds[i].Number + " selected config " + full.Config.Id +
                ": full net $" + F(full.Net) + ", ES trades " + full.EsTrades +
                ", MES proxy trades " + full.MesTrades +
                ", compared with its own OOS test net $" + F(testNetByFold[i]));
        }
        summary.Add("LATEST TRAINING WINNER FULL-DATASET NET P&L: $" + F(latestFull.Net));
        summary.Add("Gross: $" + F(allGross) + "; estimated costs: $" + F(allCosts));
        summary.Add("Wins/losses: " + won + "/" + lost);
        summary.Add("Max cumulative closed-trade drawdown: $" + F(maxDrawdown));
        summary.Add("Profitable test blocks: " + positiveFolds + "/" + folds.Length);
        summary.Add("Most recent fold's TRAINING winner (candidate, not proven): " + latestConfig.Id);
        summary.Add(HumanSettings(latestConfig));
        summary.Add("");
        summary.Add("Full-dataset replays include training dates. The stitched OOS total contains only nonoverlapping test blocks.");
        summary.Add("Full-dataset fixed-configuration results are NOT additive across folds.");
        summary.Add("All walk-forward test dates are later than their corresponding training dates.");
        summary.Add("Test-period P&L is never used to choose the fold's configuration.");
        summary.Add("The first ~40% of sessions are training-only; later ~60% are tested in three consecutive nonoverlapping blocks.");
        summary.Add("MES rows are ES-price proxies (not actual MES market data).");
        summary.Add("Quote-level stop ordering and real broker fills are unavailable; costs are estimates.");

        // Save the complete trade-by-trade replay of the latest chosen
        // configuration independently of the walk-forward test trade file.
        decimal fullRunning = 0m;
        var latestTradeRows = new List<string> {
            "date_et,instrument,source_contract,quantity,direction,entry_time_et,entry_price,exit_time_et,exit_price,exit_reason,mfe_points,gross_pnl_dollars,estimated_cost_dollars,net_pnl_dollars,cumulative_full_net_pnl_dollars,period"
        };
        foreach (var trade in latestFull.DetailedTrades ?? [])
        {
            fullRunning += trade.Net;
            var firstTestDay = sessions[folds[0].TestStart].Day;
            var period = trade.Date < firstTestDay ? "initial_training_only" :
                trade.Date < sessions[folds[1].TestStart].Day ? "test_block_1" :
                trade.Date < sessions[folds[2].TestStart].Day ? "test_block_2" :
                "test_block_3";
            latestTradeRows.Add(string.Join(",",
                Day(trade.Date), trade.IsMes ? "MES_ES_PRICE_PROXY" : "ES",
                trade.Contract, trade.Quantity.ToString(CI), trade.Direction,
                Et(trade.EntryAt), F(trade.Entry), Et(trade.ExitAt), F(trade.Exit),
                trade.ExitReason, F(trade.MfePoints), F(trade.Gross),
                F(trade.EstimatedCosts), F(trade.Net), F(fullRunning), period));
        }
        File.WriteAllLines(latestFullTradesPath, latestTradeRows);

        // Trade-by-trade report for the actual overall optimized winner,
        // which is selected from the full grid rather than the fold winners.
        decimal optimalRunning = 0m;
        var optimalTradeRows = new List<string> {
            "date_et,instrument,source_contract,quantity,direction,entry_time_et,entry_price,exit_time_et,exit_price,exit_reason,mfe_points,gross_pnl_dollars,estimated_cost_dollars,net_pnl_dollars,cumulative_full_net_pnl_dollars"
        };
        foreach (var trade in fullBest.DetailedTrades ?? [])
        {
            optimalRunning += trade.Net;
            optimalTradeRows.Add(string.Join(",",
                Day(trade.Date), trade.IsMes ? "MES_ES_PRICE_PROXY" : "ES",
                trade.Contract, trade.Quantity.ToString(CI), trade.Direction,
                Et(trade.EntryAt), F(trade.Entry), Et(trade.ExitAt), F(trade.Exit),
                trade.ExitReason, F(trade.MfePoints), F(trade.Gross),
                F(trade.EstimatedCosts), F(trade.Net), F(optimalRunning)));
        }
        File.WriteAllLines(optimalFullTradesPath, optimalTradeRows);

        File.WriteAllLines(optimalFullSummaryPath, new[] {
            "BEST OVERALL FULL-DATASET CONFIGURATION",
            "Dataset: " + Path.GetFileName(csvPath),
            "Available opening-range sessions: " + sessions.Length,
            "Dates: " + sessions[0].Day + " through " + sessions[^1].Day,
            "Configurations evaluated: " + evaluated,
            "Qualifying configurations: " + eligibleFullCount,
            "Minimum ES trade count to qualify: " + minimumFullSampleTrades,
            "Winning config ID: " + fullBest.Config.Id,
            "FULL-DATASET NET P&L: $" + F(fullBest.Net),
            "Gross: $" + F(fullBest.Gross) +
                "; estimated costs: $" + F(fullBest.Costs),
            "ES trades: " + fullBest.EsTrades +
                "; MES proxy trades: " + fullBest.MesTrades +
                "; winning trades: " + fullBest.WinningTrades,
            "Max closed-trade drawdown: $" + F(fullBest.MaxDrawdown),
            HumanSettings(fullBest.Config),
            "Walk-forward OOS P&L (separately selected fold winners): $" +
                F(runningNet)
        });

        // Diagnostic only: compare entry-time conditions for wins AND losses.
        // No patterns are fed back into optimizer selection or test P&L.
        summary.AddRange(TradePatternAnalyzer.Analyze(
            directory, sessions, combinedOutOfSampleTrades));
        File.WriteAllLines(summaryPath, summary);

        Console.WriteLine();
        Console.WriteLine("Full-grid optimal configuration: " + optimalFullSummaryPath);
        Console.WriteLine("Full-grid optimal trades: " + optimalFullTradesPath);
        Console.WriteLine("Full-dataset comparison: " + fullComparisonPath);
        Console.WriteLine("Latest config full-dataset trades: " + latestFullTradesPath);
        Console.WriteLine("Fold results: " + foldsPath);
        Console.WriteLine("Out-of-sample trades: " + tradesPath);
        Console.WriteLine("Out-of-sample monthly P&L: " + monthsPath);
        Console.WriteLine("All configuration fold and full-dataset scores: " + gridPath);
        Console.WriteLine("Walk-forward summary and latest settings: " + summaryPath);
        return 0;
    }

    private static bool IsBetter(OptimizationResult candidate, OptimizationResult? current) =>
        current is null ||
        candidate.Net > current.Net ||
        candidate.Net == current.Net && candidate.MaxDrawdown < current.MaxDrawdown ||
        candidate.Net == current.Net && candidate.MaxDrawdown == current.MaxDrawdown &&
        candidate.EsTrades > current.EsTrades ||
        candidate.Net == current.Net && candidate.MaxDrawdown == current.MaxDrawdown &&
        candidate.EsTrades == current.EsTrades && candidate.Config.Id < current.Config.Id;

    private static string HumanSettings(OptimizationConfig c) =>
        "Entry cutoff ET: " + c.Cutoff.ToString(@"hh\:mm") +
        "; Trading days ET: " + WeekdaySelection.Format(c.WeekdayMask) +
        "; OR size: " + F(c.Range.Min) + "-" + F(c.Range.Max) + " ES points" +
        "; VWAP: " + c.Vwap +
        "; Breakout buffer: " + F(c.BreakoutPoints) + " ES points" +
        "; Volume threshold: " + (c.VolumeMultiplier == 0m ? "off" : F(c.VolumeMultiplier) + "x") +
        "; Retest: " + c.Retest +
        "; First position: " + c.EsQuantity + " ES" +
        "; Initial risk cap/fallback: $" + F(c.Risk.MaxRisk) + "/$" + F(c.Risk.FallbackRisk) +
        "; ES Stage-5 trailing: $" + F(c.EsTrailDollars) +
        "; Optional Stage-6: " + c.MesQuantity + " MES (ES price proxy)" +
        "; Economic event rule: " + c.EventRule;

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CI);
    private static string F(decimal value) => value.ToString("0.00", CI);
    private static string Et(DateTimeOffset timestamp) => Orb.MarketTime.ToEastern(timestamp)
        .ToString("yyyy-MM-ddTHH:mm:sszzz", CI);
}
