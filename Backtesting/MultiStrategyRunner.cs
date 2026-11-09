using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

public sealed record MultiScore(
    int Trades, int Wins, decimal Net, decimal Gross, decimal Costs,
    decimal Drawdown, decimal ProfitFactor)
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
            drawdown, losses > 0m ? gains / losses : 0m);
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
    private sealed record Choice(MultiStrategyVariant Variant, MultiScore Score);
    private sealed record PortfolioOutcome(
        MultiPortfolioPolicy Policy, MultiScore Score, List<MultiStrategyTrade> Trades);

    public static int Run(List<PriceBar> active, List<PriceBar> all, string root)
    {
        var dir = Path.Combine(root, "data");
        Console.WriteLine();
        Console.WriteLine("=== MULTI-STRATEGY ES RESEARCH (historical, no orders) ===");
        var sessions = MultiStrategyData.Build(active, all, dir);
        if (sessions.Count < 20)
        {
            Console.Error.WriteLine("Need >=20 validated full 390-minute RTH sessions.");
            return 2;
        }
        if (sessions[0].Day < new DateOnly(2025, 10, 6) ||
            sessions[^1].Day > new DateOnly(2026, 10, 30))
            Console.WriteLine("WARNING: economic calendar does not cover every tested date.");

        var eligibleDays = sessions.Select(x => x.Day).ToHashSet();
        var orb = OptimizationData.Prepare(active, all)
            .Where(x => x.Range is not null && eligibleDays.Contains(x.Day))
            .OrderBy(x => x.Day).ToArray();
        var folds = WalkForwardFold.Create(sessions.Count);
        var variants = MultiStrategyEngine.EnumerateVariants();
        var byKind = variants.GroupBy(v => v.Kind)
            .ToDictionary(g => g.Key, g => g.ToArray());
        Console.WriteLine("Multi-strategy grid: " + variants.Count +
                          " configurations, 7 strategy types, " + sessions.Count +
                          " validated sessions, 3 walk-forward folds.");
        Console.WriteLine("Overnight 18:00-08:00; morning 09:45-11:30; afternoon 13:00-15:55 ET.");
        Console.WriteLine("1 ES per new strategy; $600 max initial risk; $10 estimated round-trip cost.");
        Console.WriteLine("No overlapping positions; up to 3 trades/day; no fresh orders after " +
                          "realized daily P&L <= -$1000.");

        OptimizationEngine.ClearCaches();
        var tradesById = new Dictionary<int, IReadOnlyDictionary<DateOnly, MultiStrategyTrade>>();
        foreach (var v in variants)
        {
            tradesById.Add(v.Id, MultiStrategyEngine.Generate(v, sessions, orb));
            if (v.Id % 50 == 0)
                Console.WriteLine("Completed multi-strategy configurations " +
                                  v.Id + "/" + variants.Count);
        }

        var grid = new List<string> {
            "fold,period,strategy,variant_id,trades,wins,win_rate_pct,net,gross,costs,drawdown,profit_factor,average_trade,parameters"
        };
        var individuals = new List<string> {
            "fold,strategy,variant_id,train_start,train_end,test_start,test_end,train_trades,train_net,train_win_pct,train_drawdown,test_trades,test_net,test_win_pct,test_drawdown,parameters"
        };
        var comparisons = new List<string> {
            "fold,policy,selected,train_start,train_end,test_start,test_end,train_trades,train_net,train_win_pct,train_drawdown,test_trades,test_net,test_win_pct,test_drawdown,test_profit_factor"
        };
        var allOosTrades = new List<(int Fold, MultiStrategyTrade Trade)>();
        var summary = new List<string> {
            "MULTI-STRATEGY ES WALK-FORWARD RESEARCH",
            "Dataset: dbo.MES_DATA (ES one-minute OHLCV)",
            "Eligible dates: " + Day(sessions[0].Day) + " through " + Day(sessions[^1].Day),
            "Valid RTH sessions: " + sessions.Count,
            "Valid overnight sessions: " + sessions.Count(s => s.Overnight is not null),
            "Candidate strategies: " + byKind.Count + "; configurations: " + variants.Count,
            "Each fold selects configurations and routing using earlier training dates only.",
            "Trade entry: next bar OPEN after completed signal; stop-first on ambiguous candle.",
            "Daily realized-loss guard: -$1000; maximum three sequential trades per trading date.",
            "All net figures deduct $10/ES per completed trade; one ES per new strategy.",
            "Full-dataset winners are IN-SAMPLE, not future validation.",
            ""
        };

        for (var foldIndex = 0; foldIndex < folds.Length; foldIndex++)
        {
            var fold = folds[foldIndex];
            var winners = new Dictionary<MultiStrategyKind, Choice>();
            var minTrainTrades = Math.Max(6, fold.TrainingSessions / 18);
            foreach (var kind in Enum.GetValues<MultiStrategyKind>())
            {
                Choice? best = null;
                foreach (var v in byKind[kind])
                {
                    var score = MultiScore.From(TradesFor(v, sessions, fold.TrainingStart,
                        fold.TrainingEnd, tradesById));
                    grid.Add(ScoreRow(fold.Number, "TRAIN", v, score));
                    if (score.Trades >= minTrainTrades &&
                        (best is null || Better(score, v.Id, best.Score, best.Variant.Id)))
                        best = new Choice(v, score);
                }
                if (best is null)
                {
                    summary.Add("Fold " + fold.Number + " " + kind +
                                ": no qualified training configuration.");
                    continue;
                }
                winners.Add(kind, best);
                var test = MultiScore.From(TradesFor(best.Variant, sessions,
                    fold.TestStart, fold.TestEnd, tradesById));
                individuals.Add(string.Join(",",
                    fold.Number, kind, best.Variant.Id,
                    Day(sessions[fold.TrainingStart].Day), Day(sessions[fold.TrainingEnd - 1].Day),
                    Day(sessions[fold.TestStart].Day), Day(sessions[fold.TestEnd - 1].Day),
                    best.Score.Trades, F(best.Score.Net), F(best.Score.WinRate),
                    F(best.Score.Drawdown), test.Trades, F(test.Net), F(test.WinRate),
                    F(test.Drawdown), best.Variant.Parameters));
                Console.WriteLine("Fold " + fold.Number + " " + kind +
                                  ": train $" + F(best.Score.Net) +
                                  " -> next-block TEST $" + F(test.Net) +
                                  " (" + test.Trades + " trades), config " + best.Variant.Id);
                summary.Add("Fold " + fold.Number + " " + kind +
                            ": TRAIN $" + F(best.Score.Net) +
                            " -> unseen TEST $" + F(test.Net) +
                            ", config " + best.Variant.Id);
            }

            // Fit the routing policy exclusively on this fold's TRAIN period.
            var proposals = new List<PortfolioOutcome>();
            foreach (var kind in winners.Keys)
                proposals.Add(EvaluatePortfolio(new MultiPortfolioPolicy(
                    MultiPortfolioMode.Single, kind), winners, sessions,
                    fold.TrainingStart, fold.TrainingEnd, tradesById));
            proposals.Add(EvaluatePortfolio(new MultiPortfolioPolicy(
                MultiPortfolioMode.FixedSessions), winners, sessions,
                fold.TrainingStart, fold.TrainingEnd, tradesById));
            foreach (var night in new[] { 1m, 2m })
            foreach (var orbThreshold in new[] { 2m, 3m })
            foreach (var morning in new[] { 1m, 2m })
            foreach (var afternoon in new[] { 2m, 3m })
                proposals.Add(EvaluatePortfolio(new MultiPortfolioPolicy(
                    MultiPortfolioMode.AdaptiveRegimes, null, night,
                    orbThreshold, morning, afternoon), winners, sessions,
                    fold.TrainingStart, fold.TrainingEnd, tradesById));

            var qualifies = proposals.Where(x =>
                x.Score.Trades >= Math.Max(6, fold.TrainingSessions / 18)).ToArray();
            var bestPolicy = (qualifies.Length > 0 ? qualifies : proposals.ToArray())
                .OrderByDescending(x => x.Score.Net)
                .ThenBy(x => x.Score.Drawdown)
                .ThenByDescending(x => x.Score.Trades)
                .ThenBy(x => x.Policy.Description, StringComparer.Ordinal)
                .First();

            foreach (var proposal in proposals)
            {
                var test = EvaluatePortfolio(proposal.Policy, winners,
                    sessions, fold.TestStart, fold.TestEnd, tradesById);
                comparisons.Add(string.Join(",",
                    fold.Number, proposal.Policy.Description,
                    proposal.Policy == bestPolicy.Policy ? "yes" : "no",
                    Day(sessions[fold.TrainingStart].Day),
                    Day(sessions[fold.TrainingEnd - 1].Day),
                    Day(sessions[fold.TestStart].Day),
                    Day(sessions[fold.TestEnd - 1].Day),
                    proposal.Score.Trades, F(proposal.Score.Net),
                    F(proposal.Score.WinRate), F(proposal.Score.Drawdown),
                    test.Score.Trades, F(test.Score.Net), F(test.Score.WinRate),
                    F(test.Score.Drawdown), F(test.Score.ProfitFactor)));
            }

            var oos = EvaluatePortfolio(bestPolicy.Policy, winners, sessions,
                fold.TestStart, fold.TestEnd, tradesById);
            allOosTrades.AddRange(oos.Trades.Select(x => (fold.Number, x)));
            Console.WriteLine("Fold " + fold.Number + " SELECTED PORTFOLIO: " +
                bestPolicy.Policy.Description + ", TRAIN $" + F(bestPolicy.Score.Net) +
                ", next-block OOS $" + F(oos.Score.Net) +
                ", " + oos.Score.Trades + " trades");
            summary.Add("Fold " + fold.Number + " PORTFOLIO: " +
                bestPolicy.Policy.Description + ", TRAIN $" + F(bestPolicy.Score.Net) +
                ", OOS $" + F(oos.Score.Net) + ", trades " + oos.Score.Trades +
                ", win rate " + F(oos.Score.WinRate) + "%");
            summary.Add("");
        }

        // Historical full-sample selection is descriptive ONLY.
        var fullWinners = new List<string> {
            "strategy,config_id,trades,wins,win_rate_pct,net,gross,costs,drawdown,profit_factor,average_net_per_trade,parameters"
        };
        summary.Add("FULL-DATASET STRATEGY WINNERS (IN-SAMPLE; NOT INDEPENDENT VALIDATION)");
        foreach (var kind in Enum.GetValues<MultiStrategyKind>())
        {
            Choice? best = null;
            var minimum = Math.Max(10, sessions.Count / 12);
            foreach (var v in byKind[kind])
            {
                var score = MultiScore.From(TradesFor(v, sessions,
                    0, sessions.Count, tradesById));
                grid.Add(ScoreRow(0, "FULL_IN_SAMPLE", v, score));
                if (score.Trades >= minimum &&
                    (best is null || Better(score, v.Id, best.Score, best.Variant.Id)))
                    best = new Choice(v, score);
            }
            if (best is null)
            {
                summary.Add("  " + kind + ": no variant met " + minimum + " trades.");
                continue;
            }
            var a = best.Score;
            fullWinners.Add(string.Join(",", kind, best.Variant.Id, a.Trades,
                a.Wins, F(a.WinRate), F(a.Net), F(a.Gross), F(a.Costs),
                F(a.Drawdown), F(a.ProfitFactor), F(a.Average),
                best.Variant.Parameters));
            summary.Add("  " + kind + ": config " + best.Variant.Id +
                ", net $" + F(a.Net) + ", win rate " + F(a.WinRate) + "%" +
                ", trades " + a.Trades + ", drawdown $" + F(a.Drawdown) +
                ", profit factor " + F(a.ProfitFactor));
            summary.Add("    " + best.Variant.Parameters);
        }

        var oosOrdered = allOosTrades.OrderBy(x => x.Trade.EntryAt).ToArray();
        var totalOos = MultiScore.From(oosOrdered.Select(x => x.Trade));
        var tradeRows = new List<string> {
            "fold,session_date_et,strategy,config_id,contract,direction,entry_et,exit_et,entry_price,exit_price,exit_reason,gross,costs,net,cumulative_oos_net"
        };
        decimal running = 0m;
        foreach (var item in oosOrdered)
        {
            var t = item.Trade;
            running += t.Net;
            tradeRows.Add(string.Join(",", item.Fold, Day(t.Day), t.Kind, t.VariantId,
                t.Contract, t.Side, Et(t.EntryAt), Et(t.ExitAt), F(t.Entry),
                F(t.Exit), t.ExitReason, F(t.Gross), F(t.Costs),
                F(t.Net), F(running)));
        }
        var monthly = new List<string> { "month_et,trades,wins,net,cumulative_net" };
        decimal monthlyNet = 0m;
        foreach (var group in oosOrdered.GroupBy(x => (x.Trade.Day.Year, x.Trade.Day.Month))
                     .OrderBy(x => x.Key.Year).ThenBy(x => x.Key.Month))
        {
            var month = MultiScore.From(group.Select(x => x.Trade));
            monthlyNet += month.Net;
            monthly.Add(group.Key.Year.ToString("D4", Ci) + "-" +
                group.Key.Month.ToString("D2", Ci) + "," + month.Trades + "," +
                month.Wins + "," + F(month.Net) + "," + F(monthlyNet));
        }

        summary.Add("");
        summary.Add("SELECTED WALK-FORWARD OOS NET $" + F(totalOos.Net) +
            ", trades " + totalOos.Trades +
            ", win rate " + F(totalOos.WinRate) + "%" +
            ", closed-trade drawdown $" + F(totalOos.Drawdown) +
            ", estimated costs $" + F(totalOos.Costs));
        summary.Add("Full-sample winners are not OOS. Compare policy candidates including " +
                    "best single, fixed multi-session and adaptive in the portfolio CSV.");
        summary.Add("Trades only use pre-entry information. Rules are fitted on earlier folds, " +
                    "but reusing an explored dataset remains prone to research overfitting.");

        File.WriteAllLines(Path.Combine(dir, "multi_strategy_candidate_scores.csv"), grid);
        File.WriteAllLines(Path.Combine(dir, "multi_strategy_individual_walkforward.csv"), individuals);
        File.WriteAllLines(Path.Combine(dir, "multi_strategy_portfolio_walkforward.csv"), comparisons);
        File.WriteAllLines(Path.Combine(dir, "multi_strategy_portfolio_trades.csv"), tradeRows);
        File.WriteAllLines(Path.Combine(dir, "multi_strategy_portfolio_monthly.csv"), monthly);
        File.WriteAllLines(Path.Combine(dir, "multi_strategy_full_sample_winners.csv"), fullWinners);
        File.WriteAllLines(Path.Combine(dir, "multi_strategy_summary.txt"), summary);

        Console.WriteLine();
        Console.WriteLine("=== MULTI-STRATEGY WALK-FORWARD SUMMARY ===");
        Console.WriteLine("Selected portfolio OOS: net $" + F(totalOos.Net) +
            ", " + totalOos.Trades + " trades, " + F(totalOos.WinRate) +
            "% wins, drawdown $" + F(totalOos.Drawdown));
        Console.WriteLine("See data/multi_strategy_summary.txt for each strategy and fold.");
        Console.WriteLine("Detailed CSV reports: multi_strategy_full_sample_winners.csv, " +
                          "multi_strategy_portfolio_walkforward.csv, " +
                          "multi_strategy_individual_walkforward.csv, " +
                          "multi_strategy_portfolio_trades.csv, " +
                          "multi_strategy_data_quality.csv");
        return 0;
    }

    private static string ScoreRow(int fold, string period,
        MultiStrategyVariant variant, MultiScore score) =>
        string.Join(",", fold, period, variant.Kind, variant.Id,
            score.Trades, score.Wins, F(score.WinRate), F(score.Net),
            F(score.Gross), F(score.Costs), F(score.Drawdown),
            F(score.ProfitFactor), F(score.Average), variant.Parameters);

    private static IEnumerable<MultiStrategyTrade> TradesFor(
        MultiStrategyVariant variant, IReadOnlyList<MultiSession> sessions,
        int start, int end,
        IReadOnlyDictionary<int, IReadOnlyDictionary<DateOnly, MultiStrategyTrade>> byId)
    {
        var source = byId[variant.Id];
        for (var i = start; i < end; i++)
            if (source.TryGetValue(sessions[i].Day, out var trade))
                yield return trade;
    }

    private static PortfolioOutcome EvaluatePortfolio(
        MultiPortfolioPolicy policy, IReadOnlyDictionary<MultiStrategyKind, Choice> winners,
        IReadOnlyList<MultiSession> sessions, int from, int to,
        IReadOnlyDictionary<int, IReadOnlyDictionary<DateOnly, MultiStrategyTrade>> byId)
    {
        var chosenTrades = new List<MultiStrategyTrade>();
        var bestBySlot = winners.Values
            .Where(c => c.Score.Net > 0m)
            .GroupBy(c => c.Variant.Slot)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score.Net)
                .ThenBy(c => c.Score.Drawdown).ThenBy(c => c.Variant.Id)
                .First().Variant.Kind);

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
                        wanted.Add(Math.Abs(day.FirstHourOvernightMove) >=
                            policy.NightThreshold * day.FirstHourOvernightAtr
                            ? MultiStrategyKind.OvernightMomentum :
                              MultiStrategyKind.OvernightReversion);
                    if (day.EarlyAtr > 0m)
                    {
                        if (day.OpeningWidth >= policy.OrbThreshold * day.EarlyAtr &&
                            day.EarlyVolumeRatio >= 0.8m)
                            wanted.Add(MultiStrategyKind.Orb);
                        else if (Math.Abs(day.OpeningMove) >=
                                 policy.MorningThreshold * day.EarlyAtr)
                            wanted.Add(MultiStrategyKind.MorningMomentum);
                        else
                            wanted.Add(MultiStrategyKind.MorningVwapReversion);
                    }
                    if (day.AfternoonAtr > 0m)
                        wanted.Add(Math.Abs(day.AfternoonMove) >=
                            policy.AfternoonThreshold * day.AfternoonAtr
                            ? MultiStrategyKind.AfternoonMomentum :
                              MultiStrategyKind.AfternoonVwapReversion);
                    break;
            }

            var proposals = new List<MultiStrategyTrade>();
            foreach (var kind in wanted)
                if (winners.TryGetValue(kind, out var chosen) &&
                    chosen.Score.Net > 0m &&
                    byId[chosen.Variant.Id].TryGetValue(day.Day, out var trade))
                    proposals.Add(trade);

            DateTimeOffset lastExit = DateTimeOffset.MinValue;
            decimal realized = 0m;
            var count = 0;
            foreach (var trade in proposals.OrderBy(t => t.EntryAt))
            {
                if (count >= MaxDailyTrades || realized <= -DailyLossStop) break;
                if (trade.EntryAt < lastExit) continue;
                chosenTrades.Add(trade);
                count++;
                lastExit = trade.ExitAt;
                realized += trade.Net;
            }
        }
        return new PortfolioOutcome(policy, MultiScore.From(chosenTrades), chosenTrades);
    }

    private static bool Better(MultiScore a, int idA, MultiScore b, int idB) =>
        a.Net > b.Net ||
        a.Net == b.Net && a.Drawdown < b.Drawdown ||
        a.Net == b.Net && a.Drawdown == b.Drawdown && a.Trades > b.Trades ||
        a.Net == b.Net && a.Drawdown == b.Drawdown &&
        a.Trades == b.Trades && idA < idB;

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", Ci);
    private static string F(decimal d) => d.ToString("0.00", Ci);
    private static string Et(DateTimeOffset d) =>
        MarketTime.ToEastern(d).ToString("yyyy-MM-ddTHH:mm:sszzz", Ci);
}
