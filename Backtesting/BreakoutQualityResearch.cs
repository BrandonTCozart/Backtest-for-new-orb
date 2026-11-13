using System.Diagnostics;
using System.Globalization;
using Orb;
using Row = Backtest_for_new_orb.OpeningDirectionResearch.SessionRow;
using Result = Backtest_for_new_orb.OpeningDirectionResearch.Outcome;
using Fill = Backtest_for_new_orb.OpeningBreakoutValidation.Trade;

namespace Backtest_for_new_orb;

// Standalone, read-only, single-threaded study of P(target-before-stop).
// Models are fitted to completed signal-candle features ONLY from historical
// training. The next-minute entry/open and all subsequent price bars contribute
// to the LABEL, but never to the feature vector.
public static class BreakoutQualityResearch
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const int OpeningFeatureCount = 3;
    private const int BreakoutFeatureCount = 11;
    private const int ShapeFeatureCount = BreakoutFeatureCount + 15;
    private const int FullFeatureCount = ShapeFeatureCount + 2;
    private const int TrainingEpochs = 550;
    private const double LearningRate = 0.20;
    private const double L2Penalty = 0.035;
    private const double ProbFloor = 1e-7;
    private static readonly string[] Models =
        ["BASE", "OPENING", "BREAKOUT", "SHAPE_BREAKOUT", "FULL_WITH_PRE4"];
    private static readonly string[] Policies =
        ["ALL_BREAKS", "BREAKOUT_TRAIN_TOP25", "SHAPE_TRAIN_TOP25",
         "FULL_TRAIN_TOP25", "FULL_P50", "FULL_P60"];

    internal sealed record Observation(
        DateOnly Day, DateTimeOffset SignalBarTime, double[] Features,
        Fill Fill, bool HitTarget)
    {
        internal double Label => HitTarget ? 1.0 : 0.0;
    }

    internal static Observation? Observe(Row row)
    {
        if (!OpeningDirectionResearch.Binary(row.FirstBreakBy11) ||
            row.FirstBreakBarIndex is < 15 or > 89 ||
            row.Rth.Length != 390)
            return null;
        var i = row.FirstBreakBarIndex;
        var c = row.Rth[i].C;
        var side = (int)row.FirstBreakBy11;
        var boundary = side > 0 ? row.OpeningHigh : row.OpeningLow;
        // Verify that a signal really exists at the recorded first-break bar.
        if (side > 0 && c.H <= boundary ||
            side < 0 && c.L >= boundary)
            throw new InvalidOperationException("First-break label does not agree with candle.");
        var width = Math.Max(0.25, row.RangePoints);
        var candleWidth = Math.Max(0.25m, c.H - c.L);
        var closeLocation = (double)((row.OpeningClose - row.OpeningLow) /
            Math.Max(0.25m, row.OpeningHigh - row.OpeningLow)) * 2 - 1;
        var openingVolume = 0.0;
        for (var m = 0; m < 15; m++)
            openingVolume += Math.Max(0L, row.Rth[m].C.V);
        var relativeSignalVolume = Math.Max(0L, c.V) /
            Math.Max(1.0, openingVolume / 15.0);
        var highExtension = side > 0
            ? (double)(c.H - row.OpeningHigh)
            : (double)(row.OpeningLow - c.L);
        var breakoutCloseBeyond = side * (double)(c.C - boundary);
        var alignedBody = side * (double)((c.C - c.O) / candleWidth);
        var closeInSignalBar = side * (double)((2m * c.C - c.H - c.L) / candleWidth);
        var features = new double[FullFeatureCount];
        // 09:45-only information.
        features[0] = Clamp(closeLocation, -1, 1);
        features[1] = Clamp(row.Opening15Points / width, -2, 2);
        features[2] = Clamp(Math.Log(width / 15.0), -2, 2);
        // Completed breakout candle information; not known at 09:45.
        features[3] = side;
        features[4] = (i - 15.0) / 75.0;
        features[5] = Clamp(alignedBody, -1, 1);
        features[6] = Clamp(breakoutCloseBeyond / width, -2, 2);
        features[7] = Clamp(highExtension / width, 0, 2);
        features[8] = Clamp(Math.Log(1 + relativeSignalVolume) - Math.Log(2), -2, 2);
        features[9] = Clamp((double)(c.H - c.L) / width, 0, 3);
        features[10] = Clamp(closeInSignalBar, -1, 1);
        // Full opening close path. No post-break candle is included.
        for (var m = 0; m < 15; m++)
            features[BreakoutFeatureCount + m] = Clamp(row.Shape[m], -2, 2);
        // Premarket ending at 09:29 ET; missing marked separately, not
        // misrepresented as a genuine zero-point premarket change.
        features[ShapeFeatureCount] = row.FourAm.Available
            ? Clamp(row.FourAm.Points / (2 * width), -2, 2) : 0;
        features[ShapeFeatureCount + 1] = row.FourAm.Available ? 1 : 0;

        var trade = OpeningBreakoutValidation.Simulate(row);
        if (trade is null)
            throw new InvalidOperationException("Directional first break must have a valid fill.");
        if (trade.EntryIndex != i + 1 ||
            trade.EntryAt != row.Rth[i + 1].T ||
            trade.EntryAt != row.Rth[i].T.AddMinutes(1))
            throw new InvalidOperationException("Cannot execute inside the observed signal candle.");

        return new Observation(row.Day, row.Rth[i].T,
            features, trade,
            trade.Reason.StartsWith("target", StringComparison.Ordinal));
    }

    private static double Clamp(double x, double low, double high) =>
        Math.Clamp(x, low, high);

    internal static Observation[] Extract(IReadOnlyList<Row> sessions)
    {
        var results = new List<Observation>(sessions.Count);
        foreach (var row in sessions)
            if (Observe(row) is { } observation)
                results.Add(observation);
        return results.ToArray();
    }

    // Fixed-regularization logistic regression. Never choose iterations,
    // architecture, features, penalty or probability threshold on OOS returns.
    internal sealed class Logistic
    {
        private readonly double[] weights;

        private Logistic(int featureCount) =>
            weights = new double[featureCount + 1];

        internal double Predict(double[] x)
        {
            double score = weights[0];
            for (var i = 1; i < weights.Length; i++)
                score += weights[i] * x[i - 1];
            return 1.0 / (1.0 + Math.Exp(-Math.Clamp(score, -35, 35)));
        }

        internal static Logistic Train(
            IReadOnlyList<Observation> observations, int count)
        {
            if (observations.Count < 30)
                throw new ArgumentException("Need at least 30 completed training breakouts.");
            var model = new Logistic(count);
            // Batch gradients, with a fixed step size and L2 regularization.
            // Uses ONLY training labels; per-example results are never copied
            // from validation, older test or newer data.
            var gradients = new double[count + 1];
            for (var epoch = 0; epoch < TrainingEpochs; epoch++)
            {
                Array.Clear(gradients);
                foreach (var row in observations)
                {
                    var diff = model.Predict(row.Features) - row.Label;
                    gradients[0] += diff;
                    for (var j = 1; j <= count; j++)
                        gradients[j] += diff * row.Features[j - 1];
                }
                model.weights[0] -= LearningRate * gradients[0] / observations.Count;
                for (var j = 1; j <= count; j++)
                    model.weights[j] -= LearningRate *
                        (gradients[j] / observations.Count + L2Penalty * model.weights[j]);
            }
            return model;
        }
    }

    internal sealed class Frozen
    {
        private readonly Dictionary<string, Logistic> models;
        internal double Prior { get; }
        private readonly Dictionary<string, double> topQuartile;

        private Frozen(IReadOnlyList<Observation> training)
        {
            if (training.Count < 50)
                throw new ArgumentException("Insufficient breakout training examples.");
            Prior = training.Average(x => x.Label);
            models = new Dictionary<string, Logistic> {
                ["OPENING"] = Logistic.Train(training, OpeningFeatureCount),
                ["BREAKOUT"] = Logistic.Train(training, BreakoutFeatureCount),
                ["SHAPE_BREAKOUT"] = Logistic.Train(training, ShapeFeatureCount),
                ["FULL_WITH_PRE4"] = Logistic.Train(training, FullFeatureCount)
            };
            topQuartile = new Dictionary<string, double>();
            foreach (var name in new[] { "BREAKOUT", "SHAPE_BREAKOUT", "FULL_WITH_PRE4" })
            {
                var sorted = training.Select(x => Probability(x, name))
                    .OrderBy(x => x).ToArray();
                // Quartile chosen purely from TRAIN predicted scores; no test
                // quantiles or test P&L are used to set the filter.
                topQuartile[name] = sorted[(int)Math.Floor((sorted.Length - 1) * 0.75)];
            }
        }

        internal static Frozen Fit(IReadOnlyList<Observation> training) =>
            new(training);

        internal double Probability(Observation item, string model) =>
            model == "BASE" ? Prior : models[model].Predict(item.Features);

        internal double QuartileCutoff(string model) => topQuartile[model];
    }

    internal static bool Take(Observation observation,
        Frozen model, string policy) => policy switch
    {
        "ALL_BREAKS" => true,
        "BREAKOUT_TRAIN_TOP25" =>
            model.Probability(observation, "BREAKOUT") >=
            model.QuartileCutoff("BREAKOUT"),
        "SHAPE_TRAIN_TOP25" =>
            model.Probability(observation, "SHAPE_BREAKOUT") >=
            model.QuartileCutoff("SHAPE_BREAKOUT"),
        "FULL_TRAIN_TOP25" =>
            model.Probability(observation, "FULL_WITH_PRE4") >=
            model.QuartileCutoff("FULL_WITH_PRE4"),
        "FULL_P50" => model.Probability(observation, "FULL_WITH_PRE4") >= 0.50,
        "FULL_P60" => model.Probability(observation, "FULL_WITH_PRE4") >= 0.60,
        _ => throw new ArgumentException("Unknown quality policy: " + policy)
    };

    internal static void Report(
        Row[] historical, Row[] recent, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var split = OpeningDirectionResearch.Split(historical.Length);
        var train = Extract(historical[..split.TrainEnd]);
        var validation = Extract(historical[split.TrainEnd..split.ValidationEnd]);
        var oldTest = Extract(historical[split.ValidationEnd..]);
        var recentTest = Extract(recent);
        var frozen = Frozen.Fit(train);
        writer.WriteLine("QUALITY_METHOD,decision=at_end_of_first_unambiguous_breakout_minute," +
            "entry=next_bar_open,stop_points=10,target_points=15,exit=12:00_ET," +
            "costs_per_ES=10,model_fit=first_60pct_historical_days_only," +
            "features=opening_context+completed_breakout_bar+15_opening_closes+04am_premarket," +
            "logistic_epochs=550,learning_rate=0.20,L2=0.035," +
            "single_threaded=true");
        writer.WriteLine("QUALITY_SPLIT,period,complete_rth_sessions,usable_breaks," +
            "excluded_no_break_or_ambiguous,target_count,stop_count,noon_count");
        writer.WriteLine("QUALITY_MODEL,period,model,observations," +
            "mean_probability_pct,target_rate_pct,brier,log_loss," +
            "brier_improvement_vs_training_baseline_pct");
        writer.WriteLine("QUALITY_COMPARE,period,simpler,richer,days," +
            "simpler_brier,richer_brier,richer_brier_gain");
        writer.WriteLine("QUALITY_CALIBRATION,period,model,probability_bin,days," +
            "mean_predicted_pct,actual_target_pct,avg_trade_net_usd");
        writer.WriteLine("QUALITY_THRESHOLD,policy,probability_threshold,source");
        writer.WriteLine("QUALITY_FILTER,period,policy,eligible_breaks,trades," +
            "coverage_pct,target_hit_pct,trade_win_rate_pct,net_usd," +
            "max_drawdown_usd,profit_factor,net_if_20_cost,net_if_30_cost," +
            "target_count,stop_count,noon_count");
        writer.WriteLine("QUALITY_YEAR,period,year,policy,trades,net_usd,target_hit_pct");

        foreach (var policy in Policies.Skip(1))
        {
            var model = policy.StartsWith("BREAKOUT", StringComparison.Ordinal)
                ? "BREAKOUT" :
                policy.StartsWith("SHAPE", StringComparison.Ordinal)
                    ? "SHAPE_BREAKOUT" : "FULL_WITH_PRE4";
            var threshold = policy switch {
                "FULL_P50" => .50,
                "FULL_P60" => .60,
                _ => frozen.QuartileCutoff(model)
            };
            writer.WriteLine("QUALITY_THRESHOLD," + policy + "," +
                F(threshold) + "," +
                (policy.Contains("TOP25", StringComparison.Ordinal) ?
                    "TRAIN_PREDICTION_75TH_PERCENTILE" : "FIXED_PREDECLARED"));
        }
        foreach (var (period, dates, observations) in new[] {
            ("TRAIN", split.TrainCount, train),
            ("VALIDATION", split.ValidationCount, validation),
            ("HISTORICAL_TEST_ALREADY_SEEN", split.HistoricalCount - split.ValidationEnd, oldTest),
            ("RECENT_ALREADY_SEEN", recent.Length, recentTest)
        })
        {
            var targets = observations.Count(x => x.HitTarget);
            var stops = observations.Count(x => x.Fill.Reason.Contains("stop", StringComparison.Ordinal));
            var noon = observations.Count(x => x.Fill.Reason == "noon_exit");
            writer.WriteLine("QUALITY_SPLIT," + period + "," +
                dates + "," + observations.Length + "," +
                (dates - observations.Length) + "," +
                targets + "," + stops + "," + noon);
            foreach (var model in Models)
                ModelReport(period, observations, model);
            foreach (var (simpler, richer) in new[] {
                ("BASE", "OPENING"),
                ("OPENING", "BREAKOUT"),
                ("BREAKOUT", "SHAPE_BREAKOUT"),
                ("SHAPE_BREAKOUT", "FULL_WITH_PRE4"),
                ("BASE", "FULL_WITH_PRE4")
            })
                Compare(period, observations, simpler, richer);
            foreach (var model in new[] { "BASE", "BREAKOUT", "SHAPE_BREAKOUT", "FULL_WITH_PRE4" })
                Calibration(period, observations, model);
            foreach (var policy in Policies)
            {
                var accepted = observations.Where(x => Take(x, frozen, policy))
                    .OrderBy(x => x.Fill.EntryAt).ToArray();
                var count = accepted.Length;
                var winners = accepted.Count(x => x.Fill.Net > 0);
                var successful = accepted.Count(x => x.HitTarget);
                var net = accepted.Sum(x => x.Fill.Net);
                decimal equity = 0, peak = 0, drawdown = 0;
                foreach (var x in accepted)
                {
                    equity += x.Fill.Net;
                    peak = Math.Max(peak, equity);
                    drawdown = Math.Max(drawdown, peak - equity);
                }
                var grossWin = accepted.Sum(x => Math.Max(0m, x.Fill.Net));
                var grossLoss = accepted.Sum(x => Math.Max(0m, -x.Fill.Net));
                var pf = grossLoss > 0 ? F((double)(grossWin / grossLoss)) :
                    count == 0 ? "na" : "infinite";
                writer.WriteLine("QUALITY_FILTER," + period + "," +
                    policy + "," + observations.Length + "," + count + "," +
                    Pct(count, observations.Length) + "," +
                    Pct(successful, count) + "," + Pct(winners, count) + "," +
                    F(net) + "," + F(drawdown) + "," + pf + "," +
                    F(net - count * 10m) + "," + F(net - count * 20m) + "," +
                    accepted.Count(x => x.HitTarget) + "," +
                    accepted.Count(x => x.Fill.Reason.Contains("stop", StringComparison.Ordinal)) +
                    "," + accepted.Count(x => x.Fill.Reason == "noon_exit"));
                if (policy is "ALL_BREAKS" or "FULL_TRAIN_TOP25")
                    foreach (var year in accepted.GroupBy(x => x.Day.Year))
                        writer.WriteLine("QUALITY_YEAR," + period + "," +
                            year.Key + "," + policy + "," + year.Count() +
                            "," + F(year.Sum(x => x.Fill.Net)) + "," +
                            Pct(year.Count(x => x.HitTarget), year.Count()));
            }
        }
        writer.WriteLine("QUALITY_CAVEATS,label=target_before_stop_not_trade_profit," +
            "noon_exit_is_failure_for_target_label_even_if_profitable," +
            "stop_first_if_same_bar_touches_both," +
            "entry_open_after_completed_signal_candle," +
            "baseline_cost=10_per_ES_round_trip," +
            "training_thresholds_fixed_before_validation," +
            "historical_and_recent_test_dates_already_consulted," +
            "probabilities_require_fresh_forward_validation," +
            "NO_NEWS_INPUTS_NO_LIVE_ORDERS_NO_SQL_WRITES");
        writer.WriteLine("QUALITY_RESEARCH_COMPLETE");

        void ModelReport(string period, Observation[] data, string model)
        {
            if (data.Length == 0)
            {
                writer.WriteLine("QUALITY_MODEL," + period + "," + model + ",0,na,na,na,na,na");
                return;
            }
            var targetRate = data.Average(x => x.Label);
            double meanP = 0, brier = 0, logloss = 0;
            foreach (var item in data)
            {
                var p = Math.Clamp(frozen.Probability(item, model),
                    ProbFloor, 1 - ProbFloor);
                meanP += p;
                var error = p - item.Label;
                brier += error * error;
                logloss -= item.Label * Math.Log(p) +
                    (1 - item.Label) * Math.Log(1 - p);
            }
            brier /= data.Length;
            var baselineBrier = data.Average(x =>
                Math.Pow(frozen.Prior - x.Label, 2));
            writer.WriteLine("QUALITY_MODEL," + period + "," + model + "," +
                data.Length + "," + F(meanP * 100 / data.Length) + "," +
                F(targetRate * 100) + "," + F(brier) + "," +
                F(logloss / data.Length) + "," +
                (baselineBrier == 0 ? "na" :
                    F(100 * (baselineBrier - brier) / baselineBrier)));
        }

        void Compare(string period, Observation[] data, string simpler, string richer)
        {
            if (data.Length == 0) return;
            var simple = data.Average(x => Math.Pow(
                frozen.Probability(x, simpler) - x.Label, 2));
            var rich = data.Average(x => Math.Pow(
                frozen.Probability(x, richer) - x.Label, 2));
            writer.WriteLine("QUALITY_COMPARE," + period + "," +
                simpler + "," + richer + "," + data.Length + "," +
                F(simple) + "," + F(rich) + "," + F(simple - rich));
        }

        void Calibration(string period, Observation[] data, string model)
        {
            // Fixed probability bins. Empty bins remain visible rather than
            // pretending the model identified any high-confidence cases.
            var bounds = new[] { 0.0, .30, .40, .50, .60, .70, 1.01 };
            for (var i = 0; i < bounds.Length - 1; i++)
            {
                var lo = bounds[i];
                var hi = bounds[i + 1];
                var bucket = data.Where(x => {
                    var p = frozen.Probability(x, model);
                    return p >= lo && p < hi;
                }).ToArray();
                writer.WriteLine("QUALITY_CALIBRATION," + period + "," +
                    model + "," + F(lo) + "_to_" +
                    F(Math.Min(1, hi)) + "," + bucket.Length + "," +
                    (bucket.Length == 0 ? "na" :
                        F(bucket.Average(x => frozen.Probability(x, model)) * 100)) +
                    "," + Pct(bucket.Count(x => x.HitTarget), bucket.Length) +
                    "," + (bucket.Length == 0 ? "na" :
                        F(bucket.Average(x => x.Fill.Net))));
            }
        }
    }

    private static string F(double value) => value.ToString("0.0000", Ci);
    private static string F(decimal value) => value.ToString("0.00", Ci);
    private static string Pct(int numerator, int denominator) =>
        denominator == 0 ? "na" : F(100.0 * numerator / denominator);

    public static async Task<int> RunAsync(
        string? connectionString, string symbol, string timezone)
    {
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine("=== FIXED ES BREAKOUT QUALITY PROBABILITY RESEARCH ===");
        Console.WriteLine("Single-threaded, frozen train-only probabilities; no grid optimization.");
        var sqlTime = Stopwatch.StartNew();
        var historicalData = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timezone, tableName: "MES_DATA_2020_2025");
        var historical = OpeningDirectionResearch.Extract(historicalData.ActiveBars);
        Console.WriteLine("QUALITY_SOURCE,historical,sql_seconds=" +
            F(sqlTime.Elapsed.TotalSeconds) + ",rth_sessions=" + historical.Length);
        historicalData = null!;
        GC.Collect();
        sqlTime.Restart();
        var recentData = await SqlMarketDataLoader.LoadAsync(
            connectionString, symbol, timezone, tableName: "MES_DATA");
        var recent = OpeningDirectionResearch.Extract(recentData.ActiveBars);
        Console.WriteLine("QUALITY_SOURCE,recent,sql_seconds=" +
            F(sqlTime.Elapsed.TotalSeconds) + ",rth_sessions=" + recent.Length);
        if (historical.Length < 100 || recent.Length == 0 ||
            recent[0].Day <= historical[^1].Day)
            throw new InvalidOperationException(
                "Need nonoverlapping historical/recent sources with at least 100 full older RTH days.");
        Report(historical, recent);
        Console.WriteLine("QUALITY_TOTAL_SECONDS," + F(stopwatch.Elapsed.TotalSeconds));
        Console.WriteLine("Quality research completed; read-only, no live orders.");
        return 0;
    }
}
