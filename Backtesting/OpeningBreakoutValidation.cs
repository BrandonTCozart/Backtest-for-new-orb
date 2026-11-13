using System.Globalization;
using Orb;
using Row = Backtest_for_new_orb.OpeningDirectionResearch.SessionRow;
using Result = Backtest_for_new_orb.OpeningDirectionResearch.Outcome;

namespace Backtest_for_new_orb;

// Follow-up experiments for the existing single-threaded direction study.
// FIXED comparison settings: training-only logistic fit; K fixed by user;
// 10 ES-point stop, 15 ES-point target, entry NEXT one-minute open
// after FIRST unambiguous opening-range break, force-exit at 12:00 ET.
// No grid search, no simulation parameter tuning, no future information
// in model features, and no live execution.
internal static class OpeningBreakoutValidation
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const double CorrectedFlat = 0.25;
    private const decimal StopPoints = 10m;
    private const decimal TargetPoints = 15m;
    private const decimal RoundTripDollars = 10m;
    private const int LastEntrySignalIndex = 89; // 10:59 ET
    private const int ExitIndex = 150; // 12:00 ET, index 0 = 09:30
    private const int ExcursionMinutes = 30;
    private const int LogisticEpochs = 400;
    private const double LearningRate = 0.25;
    private const double L2 = 0.1;
    private const double SmoothingWeight = 20;

    internal sealed record Trade(
        DateOnly Day, int BreakSide, int EntryIndex,
        DateTimeOffset EntryAt, DateTimeOffset ExitAt,
        decimal Entry, decimal Exit, string Reason,
        decimal Net, double Mfe30Points, double Mae30Points);

    // B: Three pre-registered, compact 09:45 features, not all 15 line samples.
    // Binary logistic weights are frozen after training (no validation tuning).
    internal sealed class CompactLogistic
    {
        private readonly double[] weights = new double[4];

        internal static double[] Features(Row row)
        {
            var width = Math.Max(0.25, row.RangePoints);
            var location = 2.0 * (double)((row.OpeningClose -
                row.OpeningLow) / Math.Max(0.25m, row.OpeningHigh - row.OpeningLow)) - 1.0;
            return [
                1.0, // intercept
                Math.Clamp(location, -1.0, 1.0),
                Math.Clamp(row.Opening15Points / width, -2.0, 2.0),
                Math.Clamp(Math.Log(width / 15.0), -2.0, 2.0)
            ];
        }

        internal static CompactLogistic Fit(IReadOnlyList<Row> training)
        {
            var samples = training.Where(x =>
                OpeningDirectionResearch.Binary(x.FirstBreakBy11))
                .Select(x => (X: Features(x),
                    Y: x.FirstBreakBy11 == Result.Up ? 1.0 : 0.0))
                .ToArray();
            if (samples.Length < 20)
                throw new ArgumentException("Need 20+ directional training breakouts.");
            var fit = new CompactLogistic();
            for (var iter = 0; iter < LogisticEpochs; iter++)
            {
                Span<double> gradient = stackalloc double[4];
                foreach (var (x, y) in samples)
                {
                    var p = Sigmoid(fit.Score(x));
                    for (var j = 0; j < 4; j++)
                        gradient[j] += (p - y) * x[j];
                }
                for (var j = 0; j < 4; j++)
                {
                    var penalty = j == 0 ? 0 : L2 * fit.weights[j];
                    fit.weights[j] -= LearningRate *
                        (gradient[j] / samples.Length + penalty);
                }
            }
            return fit;
        }

        private double Score(double[] x) =>
            weights[0] * x[0] + weights[1] * x[1] +
            weights[2] * x[2] + weights[3] * x[3];

        private static double Sigmoid(double v) =>
            1.0 / (1.0 + Math.Exp(-Math.Clamp(v, -35, 35)));

        internal double Probability(Row row) => Sigmoid(Score(Features(row)));
    }

    internal sealed class NearestProbability
    {
        private readonly double prior;
        private readonly (int N, int Up)[] counts = new (int, int)[3];

        internal NearestProbability(IReadOnlyList<Row> training)
        {
            var valid = training.Where(x =>
                OpeningDirectionResearch.Binary(x.FirstBreakBy11)).ToArray();
            prior = valid.Length == 0 ? 0.5 :
                valid.Count(x => x.FirstBreakBy11 == Result.Up) / (double)valid.Length;
            foreach (var x in valid)
            {
                var bin = Nearest(x) + 1;
                counts[bin].N++;
                if (x.FirstBreakBy11 == Result.Up) counts[bin].Up++;
            }
        }

        internal double Probability(Row row)
        {
            var (n, up) = counts[Nearest(row) + 1];
            return (up + SmoothingWeight * prior) / (n + SmoothingWeight);
        }
    }

    internal sealed class Frozen
    {
        internal required OpeningDirectionResearch.FrozenProbabilities Shape { get; init; }
        internal required NearestProbability NearestCalibrated { get; init; }
        internal required CompactLogistic Compact { get; init; }

        internal static Frozen Fit(IReadOnlyList<Row> training, int groups)
        {
            var clusters = OpeningDirectionResearch.ShapeFit.Train(training, groups);
            return new Frozen {
                Shape = new OpeningDirectionResearch.FrozenProbabilities(
                    training, clusters,
                    OpeningDirectionResearch.Target.FirstBreakBy11),
                NearestCalibrated = new NearestProbability(training),
                Compact = CompactLogistic.Fit(training)
            };
        }

        internal double Probability(Row row, string model) => model switch
        {
            "BASE" => Shape.BaselineUp,
            "NEAREST_CAL" => NearestCalibrated.Probability(row),
            "COMPACT" => Compact.Probability(row),
            "SHAPE" => Shape.Probability(row, "SHAPE"),
            _ => throw new ArgumentException("Unknown baseline model: " + model)
        };
    }

    // Nearest boundary at 09:45. 0 means exactly equidistant and abstain.
    // This rule is NOT fitted, optimized, or trained on breakout outcomes.
    internal static int Nearest(Row row)
    {
        var distanceHigh = row.OpeningHigh - row.OpeningClose;
        var distanceLow = row.OpeningClose - row.OpeningLow;
        return distanceHigh < distanceLow ? 1 :
            distanceLow < distanceHigh ? -1 : 0;
    }

    private static Result Side(double p) =>
        p >= 0.5 ? Result.Up : Result.Down;
    private static bool IsEligible(Row row) =>
        OpeningDirectionResearch.Binary(row.FirstBreakBy11);

    internal static Trade? Simulate(Row row)
    {
        if (!IsEligible(row) ||
            row.FirstBreakBarIndex is < 15 or > LastEntrySignalIndex ||
            row.Rth.Length < 390)
            return null;
        // A high/low breach in minute T is only confirmed after T completes.
        // No fill inside T; enter at the OPEN of the next minute (T+1).
        var side = (int)row.FirstBreakBy11;
        var index = row.FirstBreakBarIndex + 1;
        var entry = row.Rth[index].C.O;
        var stop = entry - side * StopPoints;
        var target = entry + side * TargetPoints;
        var exit = row.Rth[ExitIndex].C.C;
        var exitAt = row.Rth[ExitIndex].T;
        var reason = "noon_exit";
        for (var i = index; i <= ExitIndex; i++)
        {
            var candle = row.Rth[i].C;
            // Stops fill at the worse actual open after a gap. Favorable
            // gaps do NOT receive improvement beyond the fixed target.
            var gapStop = side == 1 ? candle.O <= stop : candle.O >= stop;
            var gapTarget = side == 1 ? candle.O >= target : candle.O <= target;
            var hitStop = side == 1 ? candle.L <= stop : candle.H >= stop;
            var hitTarget = side == 1 ? candle.H >= target : candle.L <= target;
            if (gapStop)
            {
                exit = candle.O;
                exitAt = row.Rth[i].T;
                reason = "stop_gap_open";
                break;
            }
            if (gapTarget)
            {
                exit = target;
                exitAt = row.Rth[i].T;
                reason = "target_gap_open";
                break;
            }
            if (hitStop)
            {
                exit = stop;
                exitAt = row.Rth[i].T;
                reason = hitTarget ? "stop_first_ambiguous" : "stop";
                break;
            }
            if (hitTarget)
            {
                exit = target;
                exitAt = row.Rth[i].T;
                reason = "target";
                break;
            }
        }

        // Fixed 30-minute post-entry horizon: *descriptive* MFE/MAE only.
        // Includes bar highs/lows even if the hypothetical trade would
        // have stopped earlier; it does not establish intrabar event order.
        double mfe = 0, mae = 0;
        for (var i = index; i < Math.Min(390, index + ExcursionMinutes); i++)
        {
            var c = row.Rth[i].C;
            var favorable = side == 1 ? c.H - entry : entry - c.L;
            var adverse = side == 1 ? entry - c.L : c.H - entry;
            mfe = Math.Max(mfe, (double)favorable);
            mae = Math.Max(mae, (double)adverse);
        }
        var points = side * (exit - entry);
        return new Trade(row.Day, side, index,
            row.Rth[index].T, exitAt,
            entry, exit, reason,
            points * 50m - RoundTripDollars, mfe, mae);
    }

    internal static void Report(Row[] historical, Row[] recent, int groups,
        TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var split = OpeningDirectionResearch.Split(historical.Length);
        var fitted = Frozen.Fit(historical[..split.TrainEnd], groups);
        writer.WriteLine("BREAKOUT_VALIDATION_METHOD,model_fit=train_only," +
            "fixed_shape_groups=" + groups +
            ",compact_features=close_location+net_opening_move_over_range+log_range_width," +
            "compact_logistic_epochs=400,weight_decay=0.1,learning_rate=0.25," +
            "nearest_rule=closer_high_or_low_at_09:45," +
            "direction_target=first_unambiguous_break_09:45-10:59," +
            "entry=next_minute_open_after_first_break");
        writer.WriteLine("BREAKOUT_VALIDATION_PREDICT,period,model,eligible_days," +
            "predicted_days,coverage_pct,accuracy_pct,brier,confident_60_days," +
            "confident_60_accuracy_pct");
        writer.WriteLine("BREAKOUT_VALIDATION_PAIRED,period,shape_vs,matched_days," +
            "shape_correct_only,comparison_correct_only,both_correct,both_wrong," +
            "shape_accuracy_minus_comparison_pct_points");
        writer.WriteLine("BREAKOUT_VALIDATION_TRADES,period,model,eligible_sessions," +
            "unambiguous_breaks,trade_count,win_rate_pct,net_usd,max_dd_usd," +
            "profit_factor,net_if_20_cost,net_if_30_cost," +
            "average_mfe_next_30min_points,average_mae_next_30min_points," +
            "target_count,stop_count,noon_exit_count");
        writer.WriteLine("BREAKOUT_VALIDATION_NO_BREAK,period,missing_break," +
            "ambiguous_break,flat_target_excluded");

        foreach (var (name, sample) in Sections(historical, recent, split))
        {
            var valid = sample.Where(IsEligible).ToArray();
            var ambiguous = sample.Count(x => x.FirstBreakBy11 == Result.Ambiguous);
            var none = sample.Count(x => x.FirstBreakBy11 == Result.None);
            var flat = sample.Count(x => x.FirstBreakBy11 == Result.Flat);
            writer.WriteLine("BREAKOUT_VALIDATION_NO_BREAK," + name + "," +
                none + "," + ambiguous + "," + flat);

            foreach (var model in new[] {
                "BASE", "NEAREST_RULE", "NEAREST_CAL", "COMPACT", "SHAPE" })
                PrintPredict(name, valid, model);
            PrintPaired(name, valid, "NEAREST_RULE");
            PrintPaired(name, valid, "COMPACT");

            // Cache each fill once, reuse across five optional gating policies.
            // All models saw only 09:45 information before first-break trigger.
            var realized = valid.Select(x => (Row: x, Trade: Simulate(x)))
                .Where(x => x.Trade is not null).ToArray();
            foreach (var model in new[] {
                "ALL_BREAKS", "NEAREST_RULE", "COMPACT", "SHAPE", "SHAPE_CONF60" })
            {
                var trades = realized.Where(x => ShouldTrade(x.Row, model, fitted))
                    .Select(x => x.Trade!).OrderBy(x => x.EntryAt).ToArray();
                var wins = trades.Count(x => x.Net > 0m);
                var net = trades.Sum(x => x.Net);
                var grossPositive = trades.Sum(x => Math.Max(0m, x.Net));
                var grossNegative = trades.Sum(x => Math.Max(0m, -x.Net));
                decimal equity = 0m, peak = 0m, drawdown = 0m;
                foreach (var trade in trades)
                {
                    equity += trade.Net;
                    peak = Math.Max(peak, equity);
                    drawdown = Math.Max(drawdown, peak - equity);
                }
                var factor = grossNegative > 0m
                    ? F((double)(grossPositive / grossNegative)) :
                    trades.Length == 0 ? "na" : "infinite";
                writer.WriteLine("BREAKOUT_VALIDATION_TRADES," + name + "," +
                    model + "," + sample.Length + "," + valid.Length +
                    "," + trades.Length + "," + Percent(wins, trades.Length) +
                    "," + F(net) + "," + F(drawdown) + "," + factor +
                    "," + F(net - trades.Length * 10m) +
                    "," + F(net - trades.Length * 20m) +
                    "," + Average(trades.Select(x => x.Mfe30Points)) +
                    "," + Average(trades.Select(x => x.Mae30Points)) +
                    "," + trades.Count(x => x.Reason.StartsWith("target", StringComparison.Ordinal)) +
                    "," + trades.Count(x => x.Reason.Contains("stop")) +
                    "," + trades.Count(x => x.Reason == "noon_exit"));
            }
        }
        writer.WriteLine("BREAKOUT_VALIDATION_CAVEAT,TRAIN_IS_IN_SAMPLE," +
            "ALL_BREAKS_IS_REACTIVE_AFTER_CONFIRMED_BREAK," +
            "SHAPE_MODELS_NOT_TRADE_SIGNALS_VALIDATED_FOR_PROFIT," +
            "stop_first_on_ambiguous_one_minute_OHLC," +
            "fixed_10_ES_POINT_STOP_15_POINT_TARGET_12PM_EXIT," +
            "one_ES_contract_roundtrip_cost_10_USD," +
            "historical_and_recent_test_periods_previously_inspected," +
            "NO_LIVE_TRADES");
        writer.WriteLine("BREAKOUT_VALIDATION_COMPLETE");

        void PrintPredict(string period, Row[] sample, string method)
        {
            var n = 0; var right = 0; var confident = 0; var confRight = 0;
            var brier = 0.0;
            foreach (var row in sample)
            {
                if (method == "NEAREST_RULE" && Nearest(row) == 0) continue;
                var p = method == "NEAREST_RULE"
                    ? Nearest(row) == 1 ? 1.0 : 0.0 :
                    fitted.Probability(row, method);
                var actual = row.FirstBreakBy11 == Result.Up ? 1.0 : 0.0;
                var correct = Side(p) == row.FirstBreakBy11;
                n++;
                if (correct) right++;
                brier += (p - actual) * (p - actual);
                if (p >= 0.6 || p <= 0.4)
                {
                    confident++;
                    if (correct) confRight++;
                }
            }
            writer.WriteLine("BREAKOUT_VALIDATION_PREDICT," + period +
                "," + method + "," + sample.Length + "," + n +
                "," + Percent(n, sample.Length) + "," + Percent(right, n) +
                "," + (n == 0 || method == "NEAREST_RULE" ? "na" :
                    F(brier / n)) +
                "," + confident + "," + Percent(confRight, confident));
        }

        void PrintPaired(string period, Row[] sample, string comparison)
        {
            var firstOnly = 0; var otherOnly = 0; var both = 0; var neither = 0;
            foreach (var row in sample)
            {
                if (comparison == "NEAREST_RULE" && Nearest(row) == 0) continue;
                var a = Side(fitted.Probability(row, "SHAPE")) == row.FirstBreakBy11;
                var b = (comparison == "NEAREST_RULE" ?
                    Nearest(row) == (int)row.FirstBreakBy11 :
                    Side(fitted.Probability(row, comparison)) == row.FirstBreakBy11);
                if (a && !b) firstOnly++;
                else if (!a && b) otherOnly++;
                else if (a) both++;
                else neither++;
            }
            var n = firstOnly + otherOnly + both + neither;
            writer.WriteLine("BREAKOUT_VALIDATION_PAIRED," + period +
                "," + comparison + "," + n + "," + firstOnly +
                "," + otherOnly + "," + both + "," + neither +
                "," + (n == 0 ? "na" : F(100.0 * (firstOnly - otherOnly) / n)));
        }
    }

    private static bool ShouldTrade(Row row, string model, Frozen fit)
    {
        if (!IsEligible(row)) return false;
        if (model == "ALL_BREAKS") return true; // reaction at next-minute open
        var prob = model == "NEAREST_RULE"
            ? Nearest(row) == 0 ? 0.5 : Nearest(row) == 1 ? 1.0 : 0.0 :
            fit.Probability(row, model == "SHAPE_CONF60" ? "SHAPE" : model);
        if (model == "NEAREST_RULE" && Nearest(row) == 0) return false;
        if (model == "SHAPE_CONF60" && prob > 0.4 && prob < 0.6)
            return false;
        return (int)Side(prob) == (int)row.FirstBreakBy11;
    }

    private static IEnumerable<(string Name, Row[] Sample)> Sections(
        Row[] historical, Row[] recent, OpeningDirectionResearch.Partition split)
    {
        yield return ("TRAIN", historical[..split.TrainEnd]);
        yield return ("VALIDATION", historical[split.TrainEnd..split.ValidationEnd]);
        yield return ("HISTORICAL_TEST_ALREADY_SEEN", historical[split.ValidationEnd..]);
        yield return ("RECENT_ALREADY_SEEN", recent);
    }

    private static string F(double x) => x.ToString("0.0000", Ci);
    private static string F(decimal x) => x.ToString("0.00", Ci);
    private static string Percent(int a, int b) =>
        b == 0 ? "na" : F(a * 100.0 / b);
    private static string Average(IEnumerable<double> values)
    {
        var x = values.ToArray();
        return x.Length == 0 ? "na" : F(x.Average());
    }
}
