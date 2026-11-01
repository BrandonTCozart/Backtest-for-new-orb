using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Exploratory, AFTER-THE-FACT diagnostics on the trades that were already selected
/// and executed in chronological walk-forward test blocks. No rule here alters
/// entries, exits, configuration selection, or the out-of-sample P&L.
/// </summary>
public static class TradePatternAnalyzer
{
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    private sealed record Features(
        int Fold, SimulatedTrade Trade, TimeSpan EntryTime, DayOfWeek Weekday,
        decimal OrbWidth, decimal BreakoutPoints,
        decimal? DirectionalBodyFraction, decimal? DirectionalCloseLocation,
        decimal? SignalVolumeRatio, decimal? Atr14Points,
        decimal? SignalRangeVsPrior10, decimal? DirectionalVwapDistance,
        decimal? DirectionalFiveMinuteChange);

    private sealed record Criterion(string Name, Func<Features, bool?> Matches);

    private sealed record Result(
        string Name, int YesWins, int YesLosses, int NoWins, int NoLosses,
        int Unknown, decimal? YesAverageNet, decimal? NoAverageNet)
    {
        public int YesCount => YesWins + YesLosses;
        public int NoCount => NoWins + NoLosses;
        public int Count => YesCount + NoCount;
        public decimal? YesLossRate => YesCount == 0 ? null : 100m * YesLosses / YesCount;
        public decimal? NoLossRate => NoCount == 0 ? null : 100m * NoLosses / NoCount;
        public decimal? Delta => YesLossRate - NoLossRate;
        // Avoid pretending two or three trades amount to a stable pattern.
        public bool HasMinimumSupport => YesCount >= 5 && NoCount >= 5;
    }

    public static IReadOnlyList<string> Analyze(
        string folder,
        IReadOnlyList<SessionData> sessions,
        IReadOnlyList<(int FoldNumber, SimulatedTrade Trade)> testTrades)
    {
        var es = testTrades.Where(t => !t.Trade.IsMes).OrderBy(t => t.Trade.EntryAt).ToArray();
        var excludedMes = testTrades.Count(t => t.Trade.IsMes);
        var sessionsByDay = sessions.ToDictionary(s => s.Day);
        var records = new List<Features>();
        var unmatched = 0;

        foreach (var (fold, trade) in es)
        {
            if (!sessionsByDay.TryGetValue(trade.Date, out var session))
            {
                unmatched++;
                continue;
            }

            var features = Describe(fold, trade, session);
            if (features is null) unmatched++;
            else records.Add(features);
        }

        var observationsFile = Path.Combine(folder, "optimizer_trade_pattern_observations.csv");
        var comparisonsFile = Path.Combine(folder, "optimizer_trade_pattern_comparisons.csv");
        var summaryFile = Path.Combine(folder, "optimizer_trade_pattern_summary.txt");

        var observationRows = new List<string> {
            "fold,date_et,source_contract,side,quantity,entry_time_et,exit_reason,net_pnl_dollars,winning_trade,orb_width_points,breakout_beyond_or_points,signal_body_directional_fraction,signal_close_directional_location,signal_volume_vs_prior10,preentry_atr14_points,signal_range_vs_prior10,directional_vwap_distance_points,directional_five_minute_price_change_points,mfe_points"
        };
        foreach (var f in records)
        {
            observationRows.Add(string.Join(",",
                f.Fold.ToString(CI), Date(f.Trade.Date), f.Trade.Contract,
                f.Trade.Direction, f.Trade.Quantity.ToString(CI),
                Et(f.Trade.EntryAt), f.Trade.ExitReason, N(f.Trade.Net),
                f.Trade.Net > 0m ? "yes" : "no", N(f.OrbWidth),
                N(f.BreakoutPoints), Maybe(f.DirectionalBodyFraction),
                Maybe(f.DirectionalCloseLocation), Maybe(f.SignalVolumeRatio),
                Maybe(f.Atr14Points), Maybe(f.SignalRangeVsPrior10),
                Maybe(f.DirectionalVwapDistance), Maybe(f.DirectionalFiveMinuteChange),
                N(f.Trade.MfePoints)));
        }
        File.WriteAllLines(observationsFile, observationRows);

        // These are PREDECLARED, easy-to-interpret conditions. The diagnostic
        // scans neither arbitrarily optimized thresholds nor combinations of rules.
        var criteria = new Criterion[] {
            new("Monday", f => f.Weekday == DayOfWeek.Monday),
            new("Tuesday", f => f.Weekday == DayOfWeek.Tuesday),
            new("Wednesday", f => f.Weekday == DayOfWeek.Wednesday),
            new("Thursday", f => f.Weekday == DayOfWeek.Thursday),
            new("Friday", f => f.Weekday == DayOfWeek.Friday),
            new("Entry by 10:00 ET", f => f.EntryTime <= new TimeSpan(10,0,0)),
            new("Entry after 10:00 through 10:30 ET", f =>
                f.EntryTime > new TimeSpan(10,0,0) && f.EntryTime <= new TimeSpan(10,30,0)),
            new("Entry after 10:30 ET", f => f.EntryTime > new TimeSpan(10,30,0)),
            new("Long trade", f => f.Trade.Direction == TradeDirection.Long),
            new("OR width below 15 points", f => f.OrbWidth < 15m),
            new("OR width 15-30 points", f => f.OrbWidth >= 15m && f.OrbWidth <= 30m),
            new("OR width over 30 points", f => f.OrbWidth > 30m),
            new("Breakout close at least 1 point past OR", f => f.BreakoutPoints >= 1m),
            new("Breakout close at least 10% of OR width past boundary", f =>
                f.OrbWidth > 0m ? f.BreakoutPoints >= f.OrbWidth * 0.10m : null),
            new("Signal candle body >= 60% of range in trade direction", f =>
                GreaterOrEqual(f.DirectionalBodyFraction, .60m)),
            new("Signal close in directional outer 25% of candle", f =>
                GreaterOrEqual(f.DirectionalCloseLocation, .75m)),
            new("Signal volume >= 1.25x prior 10-bar average", f =>
                GreaterOrEqual(f.SignalVolumeRatio, 1.25m)),
            new("Signal candle range >= 1.5x prior 10-bar average", f =>
                GreaterOrEqual(f.SignalRangeVsPrior10, 1.5m)),
            new("14-minute ATR >= 4 ES points", f =>
                GreaterOrEqual(f.Atr14Points, 4m)),
            new("14-minute ATR >= 6 ES points", f =>
                GreaterOrEqual(f.Atr14Points, 6m)),
            new("Signal close on trade side of regular-session VWAP", f =>
                Greater(f.DirectionalVwapDistance, 0m)),
            new("Last 5-minute price movement aligned with trade", f =>
                Greater(f.DirectionalFiveMinuteChange, 0m))
        };

        var comparisons = criteria.Select(criterion =>
        {
            int yesWins = 0, yesLosses = 0, noWins = 0, noLosses = 0, unknown = 0;
            decimal yesNet = 0m, noNet = 0m;
            foreach (var f in records)
            {
                bool? has = criterion.Matches(f);
                if (has is null) { unknown++; continue; }
                bool win = f.Trade.Net > 0m; // Break-even is NOT counted as a win.
                if (has.Value)
                {
                    if (win) yesWins++; else yesLosses++;
                    yesNet += f.Trade.Net;
                }
                else
                {
                    if (win) noWins++; else noLosses++;
                    noNet += f.Trade.Net;
                }
            }
            var yesCount = yesWins + yesLosses;
            var noCount = noWins + noLosses;
            return new Result(criterion.Name, yesWins, yesLosses, noWins, noLosses,
                unknown, yesCount > 0 ? yesNet / yesCount : null,
                noCount > 0 ? noNet / noCount : null);
        }).ToArray();

        var comparisonRows = new List<string> {
            "criterion,with_total,with_wins,with_losses,with_loss_rate_pct,with_average_net_dollars,without_total,without_wins,without_losses,without_loss_rate_pct,without_average_net_dollars,loss_rate_difference_percentage_points,unknown_measurements,support_at_least_5_each_side"
        };
        foreach (var r in comparisons)
        {
            comparisonRows.Add(string.Join(",",
                Csv(r.Name), r.YesCount, r.YesWins, r.YesLosses,
                Maybe(r.YesLossRate), Maybe(r.YesAverageNet),
                r.NoCount, r.NoWins, r.NoLosses,
                Maybe(r.NoLossRate), Maybe(r.NoAverageNet),
                Maybe(r.Delta), r.Unknown,
                r.HasMinimumSupport ? "yes" : "no"));
        }
        File.WriteAllLines(comparisonsFile, comparisonRows);

        var winners = records.Count(r => r.Trade.Net > 0m);
        var losers = records.Count - winners;
        var baseline = records.Count == 0 ? 0m : 100m * losers / records.Count;

        var summary = new List<string> {
            "EXPLORATORY ORB TRADE PATTERNS — WALK-FORWARD TEST TRADES ONLY",
            "ES first trades analyzed: " + records.Count + "; winners: " + winners +
                "; losers/breakeven: " + losers,
            "Baseline loss/breakeven rate: " + N(baseline) + "%",
            "Excluded MES ES-price-proxy reversals: " + excludedMes,
            "ES trades without matching entry candle: " + unmatched,
            "",
            "Every criterion is compared against BOTH winning and losing trades.",
            "WITH = condition true; WITHOUT = condition false. Missing measurements are excluded.",
            "Positive loss-rate difference: worse outcomes WITH the condition; negative: better.",
            "Comparisons are descriptive, NOT causal or predictive evidence.",
            "Five or more WITH and WITHOUT observations is only a basic display threshold, NOT statistical significance.",
            ""
        };

        var supported = comparisons
            .Where(x => x.HasMinimumSupport && x.Delta.HasValue).ToArray();
        // Show weaker AND stronger associations, preventing one-sided reporting.
        var greater = supported.Where(x => x.Delta > 0)
            .OrderByDescending(x => x.Delta)
            .ThenByDescending(x => x.YesCount)
            .Take(4).ToArray();
        var lesser = supported.Where(x => x.Delta < 0)
            .OrderBy(x => x.Delta)
            .ThenByDescending(x => x.YesCount)
            .Take(4).ToArray();

        summary.Add("Conditions associated with HIGHER observed loss rates:");
        if (greater.Length == 0) summary.Add("  No comparisons passed the minimum sample/display checks.");
        else foreach (var r in greater) summary.Add("  " + FormatComparison(r));
        summary.Add("");
        summary.Add("Conditions associated with LOWER observed loss rates:");
        if (lesser.Length == 0) summary.Add("  No comparisons passed the minimum sample/display checks.");
        else foreach (var r in lesser) summary.Add("  " + FormatComparison(r));

        summary.Add("");
        summary.Add("ALL conditions (including overlaps, near-neutral and small-sample results): " +
            Path.GetFileName(comparisonsFile));
        summary.Add("Entry-only measurements for every test ES trade: " +
            Path.GetFileName(observationsFile));
        summary.Add("Exit reason and MFE are AFTER-THE-FACT outcomes, never used as predictive inputs.");
        summary.Add("If a condition appears in both winners and losers, that is expected; its comparative rates matter.");
        summary.Add("All criteria are correlated and the sample is small; the largest differences may be chance.");
        summary.Add("IMPORTANT: Inspecting walk-forward test trades for patterns uses those test outcomes for research.");
        summary.Add("DO NOT retune rules using these results and then call the SAME periods unseen.");
        summary.Add("Validate new hypotheses on fresh later data using a new walk-forward evaluation.");
        File.WriteAllLines(summaryFile, summary);

        Console.WriteLine();
        Console.WriteLine("TRADE-PATTERN COMPARISON (exploratory; ES first trades only)");
        Console.WriteLine("Trades: " + records.Count + " (" + winners + " winners, " +
            losers + " losses/breakeven); baseline loss rate " + N(baseline) + "%");
        foreach (var line in summary.SkipWhile(line =>
                     line != "Conditions associated with HIGHER observed loss rates:")
                     .TakeWhile(line =>
                         line != "ALL conditions (including overlaps, near-neutral and small-sample results): " +
                         Path.GetFileName(comparisonsFile)))
        {
            if (!string.IsNullOrEmpty(line)) Console.WriteLine(line);
        }
        Console.WriteLine("Each row reports BOTH winners and losers satisfying the condition.");
        Console.WriteLine("These findings are not verified predictors; validate on NEW unseen data.");
        Console.WriteLine("Pattern comparisons: " + comparisonsFile);
        Console.WriteLine("Trade observations: " + observationsFile);
        Console.WriteLine("Pattern summary: " + summaryFile);

        return [
            "",
            "POST-TEST EXPLORATORY TRADE PATTERN ANALYSIS",
            "ES samples: " + records.Count + ", wins: " + winners + ", losses/breakeven: " +
                losers + ", baseline loss/breakeven rate: " + N(baseline) + "%",
            ..greater.Select(FormatComparison),
            ..lesser.Select(FormatComparison),
            "The reported associations may also occur in winners and are NOT validated predictors.",
            "New rules proposed from these test labels must be evaluated on NEW unseen dates."
        ];
    }

    private static Features? Describe(int fold, SimulatedTrade trade, SessionData session)
    {
        int entryIndex = Array.FindIndex(session.Bars, bar => bar.T == trade.EntryAt &&
            string.Equals(bar.Contract, trade.Contract, StringComparison.OrdinalIgnoreCase));
        if (entryIndex < 1 || session.Range is not { } orb) return null;
        int signalIndex = entryIndex - 1;
        if (session.Bars[signalIndex].T != trade.EntryAt.AddMinutes(-1))
            return null;

        // Only the completed signal candle (and earlier candles) is read.
        // Entry candle high, low, close or future prices are NEVER used.
        var signal = session.Bars[signalIndex].C;
        var side = trade.Direction == TradeDirection.Long ? 1m : -1m;
        var signalRange = signal.H - signal.L;
        decimal? directionalBodyFraction = signalRange > 0m
            ? side * (signal.C - signal.O) / signalRange : null;
        decimal? directionalCloseLocation = signalRange > 0m
            ? (side > 0m ? signal.C - signal.L : signal.H - signal.C) / signalRange
            : null;

        decimal? volumeRatio = null;
        decimal? signalRangeVsPrior = null;
        if (Continuous(session, signalIndex, 10))
        {
            decimal volTotal = 0m, rangeTotal = 0m;
            for (int i = signalIndex - 10; i < signalIndex; i++)
            {
                volTotal += Math.Max(0, session.Bars[i].C.V);
                rangeTotal += session.Bars[i].C.H - session.Bars[i].C.L;
            }
            if (volTotal > 0m)
                volumeRatio = signal.V / (volTotal / 10m);
            if (rangeTotal > 0m)
                signalRangeVsPrior = signalRange / (rangeTotal / 10m);
        }

        decimal? atr14 = null;
        if (Continuous(session, signalIndex, 14))
        {
            decimal totalTrueRange = 0m;
            for (int i = signalIndex - 13; i <= signalIndex; i++)
            {
                var candle = session.Bars[i].C;
                var priorClose = session.Bars[i - 1].C.C;
                totalTrueRange += Math.Max(candle.H - candle.L,
                    Math.Max(Math.Abs(candle.H - priorClose),
                        Math.Abs(candle.L - priorClose)));
            }
            atr14 = totalTrueRange / 14m;
        }

        decimal? fiveMinuteDrift = Continuous(session, signalIndex, 5)
            ? side * (signal.C - session.Bars[signalIndex - 5].C.C) : null;
        decimal? distanceVwap = session.Vwaps[signalIndex] is decimal vwap
            ? side * (signal.C - vwap) : null;

        var entryEt = MarketTime.ToEastern(trade.EntryAt);
        return new Features(fold, trade, entryEt.TimeOfDay,
            entryEt.DayOfWeek, orb.Width,
            side > 0m ? signal.C - orb.High : orb.Low - signal.C,
            directionalBodyFraction, directionalCloseLocation, volumeRatio,
            atr14, signalRangeVsPrior, distanceVwap, fiveMinuteDrift);
    }

    // priorMinutes guarantees exact, uninterrupted same-session minutes.
    private static bool Continuous(SessionData day, int index, int priorMinutes)
    {
        if (index < priorMinutes) return false;
        for (int i = index - priorMinutes + 1; i <= index; i++)
            if (day.Bars[i].T != day.Bars[i - 1].T.AddMinutes(1))
                return false;
        return true;
    }

    private static bool? GreaterOrEqual(decimal? value, decimal threshold) =>
        value is decimal n ? n >= threshold : null;
    private static bool? Greater(decimal? value, decimal threshold) =>
        value is decimal n ? n > threshold : null;
    private static string FormatComparison(Result r) =>
        r.Name + ": WITH " + r.YesLosses + " losses / " + r.YesWins +
        " wins (" + Maybe(r.YesLossRate) + "% losses); WITHOUT " +
        r.NoLosses + " losses / " + r.NoWins + " wins (" +
        Maybe(r.NoLossRate) + "% losses); difference " +
        (r.Delta >= 0m ? "+" : "") + Maybe(r.Delta) + " pp";
    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CI);
    private static string N(decimal number) => number.ToString("0.00", CI);
    private static string Maybe(decimal? number) =>
        number is decimal n ? N(n) : "";
    private static string Et(DateTimeOffset time) => MarketTime.ToEastern(time)
        .ToString("yyyy-MM-ddTHH:mm:sszzz", CI);
}
