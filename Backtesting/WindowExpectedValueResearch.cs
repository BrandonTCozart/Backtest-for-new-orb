using System.Diagnostics;
using System.Globalization;
using Orb;
using Row = Backtest_for_new_orb.OpeningDirectionResearch.SessionRow;
using Outcome = Backtest_for_new_orb.OpeningDirectionResearch.Outcome;
using Fill = Backtest_for_new_orb.OpeningBreakoutValidation.Trade;

namespace Backtest_for_new_orb;

/// <summary>
/// Single-threaded, read-only research of 15 vs. 30 minute opening ranges,
/// their 15/30 shape interaction, and whether five completed minutes AFTER
/// the opening range contribute predictive information. Probability and
/// expected-net models are fitted on the historical training block only.
/// A future trade fill is the label, NEVER an input to the prediction.
/// </summary>
public static class WindowExpectedValueResearch
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const int LastSignalIndex = 89; // through 10:59 ET
    private const int PostMinutes = 5;
    private const int ShapeSamples = 15;
    private const int CoreCount = 13;
    private const int EarlyDetailCount = 5;
    private const int PostDetailCount = 6;
    private const double L2 = 0.07;
    private const double StepSize = 0.18;
    private const int Epochs = 400;
    private const double ScaleUsd = 500;
    private const double LabelClipUsd = 1500;
    private const double FlatPoints = 1.0;
    internal sealed record Scenario(string Name, int Minutes, int Delay)
    {
        internal int DecisionIndex => Minutes + Delay;
        internal bool HasPost => Delay == PostMinutes;
        internal bool Is30 => Minutes == 30;
    }

    internal static readonly Scenario[] Scenarios =
    [
        new("ORB15_0945", 15, 0),
        new("ORB15_WAIT5_0950", 15, 5),
        new("ORB30_1000", 30, 0),
        new("ORB30_WAIT5_1005", 30, 5)
    ];

    internal enum Exclusion { None, Incomplete, EarlyBreak, NoBreak, Ambiguous }
    internal sealed record Candidate(
        DateOnly Day, int SignalIndex, Outcome Side, int WindowMinutes,
        int DelayMinutes, string PostBehavior, double Width,
        double[] Core, double[] Shape, double[] Early, double[] Post,
        Fill Fill)
    {
        internal bool TargetFirst =>
            Fill.Reason.StartsWith("target", StringComparison.Ordinal);
        internal bool Profitable => Fill.Net > 0;
        internal double Net => (double)Fill.Net;
        internal double ClippedNet => Math.Clamp(Net, -LabelClipUsd, LabelClipUsd);
    }
    internal sealed record Observation(Candidate? Candidate, Exclusion Exclusion);

    private static double Bounded(double n, double min, double max) =>
        Math.Clamp(n, min, max);

    private static double Sigmoid(double score) =>
        1.0 / (1 + Math.Exp(-Math.Clamp(score, -35, 35)));

    private static double Pearsons(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count || a.Count < 4) return double.NaN;
        double x = 0, y = 0, xx = 0, yy = 0, xy = 0;
        for (var i = 0; i < a.Count; i++)
        {
            var xv = a[i]; var yv = b[i];
            x += xv; y += yv; xx += xv * xv; yy += yv * yv; xy += xv * yv;
        }
        var n = a.Count;
        var denominator = (n * xx - x * x) * (n * yy - y * y);
        return denominator > 1e-10
            ? (n * xy - x * y) / Math.Sqrt(denominator)
            : double.NaN;
    }

    // All data used in Core, Shape, Early and Post exist by the END of
    // the completed breakout signal minute (never its subsequent entry).
    internal static Observation Observe(Row row, Scenario scenario)
    {
        if (row.Rth.Length != 390)
            return new Observation(null, Exclusion.Incomplete);
        var candles = row.Rth;
        var length = scenario.Minutes;
        var start = scenario.DecisionIndex;
        if (length is not (15 or 30) || scenario.Delay is not (0 or 5) ||
            start > LastSignalIndex)
            throw new ArgumentException("Only fixed 15/30 minute and +0/+5 minute experiments allowed.");

        decimal high = decimal.MinValue, low = decimal.MaxValue;
        decimal volume = 0;
        var firstOpen = candles[0].C.O;
        for (var k = 0; k < length; k++)
        {
            var c = candles[k].C;
            high = Math.Max(high, c.H);
            low = Math.Min(low, c.L);
            volume += Math.Max(0L, c.V);
        }
        var windowEndClose = candles[length - 1].C.C;
        var width = Math.Max(.25m, high - low);

        // A first break of the ORIGINAL range before our delayed decision
        // invalidates a later "first breakout" claim. Do not reuse it or
        // move the clock backwards to create a fictitious trade opportunity.
        for (var i = length; i < start; i++)
            if (candles[i].C.H > high || candles[i].C.L < low)
                return new Observation(null, Exclusion.EarlyBreak);

        var signalIndex = -1;
        var side = Outcome.None;
        for (var i = start; i <= LastSignalIndex; i++)
        {
            var c = candles[i].C;
            var up = c.H > high;
            var down = c.L < low;
            if (!up && !down) continue;
            if (up && down)
                return new Observation(null, Exclusion.Ambiguous);
            side = up ? Outcome.Up : Outcome.Down;
            signalIndex = i;
            break;
        }
        if (signalIndex == -1)
            return new Observation(null, Exclusion.NoBreak);

        var sign = (int)side;
        var candle = candles[signalIndex].C;
        var boundary = sign == 1 ? high : low;
        var barRange = Math.Max(0.25m, candle.H - candle.L);
        var barBody = sign * (double)((candle.C - candle.O) / barRange);
        var barCloseLoc = sign * (double)(
            (2m * candle.C - candle.H - candle.L) / barRange);
        var extension = sign == 1 ? candle.H - high : low - candle.L;
        var beyond = sign * (candle.C - boundary);
        var openingLocation = (double)((windowEndClose - low) / width) * 2 - 1;
        var openNet = windowEndClose - firstOpen;
        var relativeVolume = (double)Math.Max(0L, candle.V) /
            Math.Max(1, (double)volume / length);

        var core = new double[CoreCount];
        core[0] = sign;
        core[1] = Bounded((signalIndex - start) / 75.0, 0, 1);
        core[2] = Bounded(Math.Log((double)width / 15.0), -2, 2);
        core[3] = Bounded(openingLocation, -1, 1);
        core[4] = Bounded((double)(openNet / width), -2, 2);
        core[5] = Bounded((double)(beyond / width), -2, 2);
        core[6] = Bounded((double)(extension / width), 0, 3);
        core[7] = Bounded(barBody, -1, 1);
        core[8] = Bounded(barCloseLoc, -1, 1);
        core[9] = Bounded(Math.Log(1 + relativeVolume) - Math.Log(2), -2, 3);
        core[10] = Bounded((double)(barRange / width), 0, 3);
        core[11] = row.FourAm.Available
            ? Bounded(row.FourAm.Points / Math.Max(5, 2 * (double)width), -2, 2)
            : 0;
        core[12] = row.FourAm.Available ? 1 : 0;

        // Fifteen *uniformly spaced* checkpoints in the completed opening
        // window. For 15 minutes: all fifteen closes. For 30: every second
        // close. This is the 30-minute path "on its own".
        var shape = new double[ShapeSamples];
        for (var j = 0; j < ShapeSamples; j++)
        {
            var bar = (j + 1) * length / ShapeSamples - 1;
            shape[j] = Bounded(
                (double)((candles[bar].C.C - firstOpen) / width), -2, 2);
        }

        // Extra "15 minute window within 30" context that could be lost
        // by representing the 30 minute window with 15 sampled checkpoints.
        var early = new double[EarlyDetailCount];
        if (scenario.Is30)
        {
            decimal earlyH = decimal.MinValue, earlyL = decimal.MaxValue;
            for (var k = 0; k < 15; k++)
            {
                earlyH = Math.Max(earlyH, candles[k].C.H);
                earlyL = Math.Min(earlyL, candles[k].C.L);
            }
            var close15 = candles[14].C.C;
            early[0] = Bounded((double)((earlyH - earlyL) / width), 0, 1);
            early[1] = Bounded((double)((close15 - firstOpen) / width), -2, 2);
            early[2] = Bounded((double)((windowEndClose - close15) / width), -2, 2);
            early[3] = Bounded((double)((close15 - earlyL) /
                Math.Max(.25m, earlyH - earlyL)), 0, 1);
            early[4] = Bounded((double)((close15 - low) / width), 0, 1);
        }

        var post = new double[PostDetailCount];
        var behavior = "NOT_OBSERVED";
        if (scenario.HasPost)
        {
            decimal postHigh = decimal.MinValue, postLow = decimal.MaxValue;
            decimal postVol = 0, postSignedVol = 0;
            for (var k = length; k < start; k++)
            {
                var pc = candles[k].C;
                postHigh = Math.Max(postHigh, pc.H);
                postLow = Math.Min(postLow, pc.L);
                var v = Math.Max(0L, pc.V);
                postVol += v;
                postSignedVol += pc.C > pc.O ? v :
                    pc.C < pc.O ? -v : 0;
            }
            var postClose = candles[start - 1].C.C;
            var change = postClose - windowEndClose;
            post[0] = Bounded((double)(change / width), -2, 2);
            post[1] = Bounded((double)((postHigh - high) / width), -2, 2);
            post[2] = Bounded((double)((low - postLow) / width), -2, 2);
            post[3] = Bounded((double)((postHigh - postLow) / width), 0, 2);
            post[4] = postVol == 0 ? 0 :
                Bounded((double)(postSignedVol / postVol), -1, 1);
            post[5] = Bounded(Math.Log(1 + (double)postVol /
                Math.Max(1, (double)volume * PostMinutes / length)) -
                Math.Log(2), -2, 2);
            var openSign = Math.Sign(openNet);
            var postSign = change > (decimal)FlatPoints ? 1 :
                change < -(decimal)FlatPoints ? -1 : 0;
            behavior = postSign == 0 ? "FLAT" :
                openSign == 0 ? "NO_OPEN_TREND" :
                postSign == openSign ? "CONTINUE" : "REVERSE";
        }

        // Actual entry takes place AFTER the confirmed signal candle.
        // All four window scenarios use the SAME conservative, fixed
        // next-open trade fill engine as the earlier breakout study.
        var shadow = row with {
            FirstBreakBy11 = side, FirstBreakBarIndex = signalIndex
        };
        var trade = OpeningBreakoutValidation.Simulate(shadow)
            ?? throw new InvalidOperationException("First break must create a valid fixed fill.");
        if (trade.EntryIndex != signalIndex + 1 ||
            trade.EntryAt != candles[signalIndex + 1].T ||
            trade.EntryAt <= candles[signalIndex].T)
            throw new InvalidOperationException("Breakout prediction contains future information.");
        return new Observation(new Candidate(
            row.Day, signalIndex, side, length, scenario.Delay, behavior,
            (double)width, core, shape, early, post, trade),
            Exclusion.None);
    }

    private static int FeatureCount(Scenario cohort, string method) =>
        method switch
        {
            "CORE" => CoreCount,
            "SHAPE" => CoreCount + ShapeSamples,
            "SHAPE_EARLY15" when cohort.Is30 =>
                CoreCount + ShapeSamples + EarlyDetailCount,
            "SHAPE_POST5" when cohort.HasPost =>
                CoreCount + ShapeSamples + PostDetailCount,
            "SHAPE_EARLY15_POST5" when cohort.HasPost && cohort.Is30 =>
                CoreCount + ShapeSamples + EarlyDetailCount + PostDetailCount,
            "BASE" => 0,
            _ => throw new ArgumentException("Not a valid model for this observation cohort.")
        };

    internal static string[] Methods(Scenario cohort) => cohort switch
    {
        { Minutes: 15, Delay: 0 } => ["BASE", "CORE", "SHAPE"],
        { Minutes: 15, Delay: 5 } => ["BASE", "CORE", "SHAPE", "SHAPE_POST5"],
        { Minutes: 30, Delay: 0 } =>
            ["BASE", "CORE", "SHAPE", "SHAPE_EARLY15"],
        { Minutes: 30, Delay: 5 } =>
            ["BASE", "CORE", "SHAPE", "SHAPE_EARLY15",
             "SHAPE_POST5", "SHAPE_EARLY15_POST5"],
        _ => throw new ArgumentException("Invalid fixed cohort.")
    };

    private static double[] Features(Candidate candidate, Scenario cohort, string model)
    {
        var count = FeatureCount(cohort, model);
        var x = new double[count];
        var o = 0;
        Array.Copy(candidate.Core, 0, x, o, candidate.Core.Length);
        o += candidate.Core.Length;
        if (count > o)
        {
            Array.Copy(candidate.Shape, 0, x, o, candidate.Shape.Length);
            o += candidate.Shape.Length;
        }
        if (model is "SHAPE_EARLY15" or "SHAPE_EARLY15_POST5")
        {
            Array.Copy(candidate.Early, 0, x, o, candidate.Early.Length);
            o += candidate.Early.Length;
        }
        if (model is "SHAPE_POST5" or "SHAPE_EARLY15_POST5")
            Array.Copy(candidate.Post, 0, x, o, candidate.Post.Length);
        return x;
    }

    internal sealed record Prediction(double ExpectedNetUsd, double TargetProbability);

    // Ridge-penalized linear EV + logistic target-before-stop probability.
    // Target net USD is winsorized ONLY while fitting to avoid a single
    // exceptional trade dominating coefficients. Raw trade net and risk
    // metrics are always calculated from the non-winsorized simulated P&L.
    internal sealed class FittedModel
    {
        private readonly double[] ev;
        private readonly double[] logistic;
        private readonly int count;

        private FittedModel(int features)
        {
            count = features;
            ev = new double[count + 1];
            logistic = new double[count + 1];
        }

        private static double Dot(double[] weight, double[] data)
        {
            var n = weight[0];
            for (var k = 1; k < weight.Length; k++)
                n += weight[k] * data[k - 1];
            return n;
        }

        internal Prediction Predict(double[] x) => new(
            Bounded(Dot(ev, x) * ScaleUsd, -LabelClipUsd, LabelClipUsd),
            Sigmoid(Dot(logistic, x)));

        internal static FittedModel Train(
            IReadOnlyList<(double[] Features, Candidate Candidate)> training,
            int number)
        {
            if (training.Count < 50)
                throw new InvalidOperationException("Need at least 50 training trades to estimate expected value.");
            var f = new FittedModel(number);
            var priorP = Bounded(training.Average(x =>
                x.Candidate.TargetFirst ? 1.0 : 0.0), 0.001, 0.999);
            f.ev[0] = training.Average(x => x.Candidate.ClippedNet) / ScaleUsd;
            f.logistic[0] = Math.Log(priorP / (1 - priorP));

            var gradientEv = new double[number + 1];
            var gradientLog = new double[number + 1];
            for (var epoch = 0; epoch < Epochs; epoch++)
            {
                Array.Clear(gradientEv);
                Array.Clear(gradientLog);
                foreach (var (x, candidate) in training)
                {
                    var dEv = Dot(f.ev, x) - candidate.ClippedNet / ScaleUsd;
                    var dLog = Sigmoid(Dot(f.logistic, x)) -
                        (candidate.TargetFirst ? 1.0 : 0.0);
                    gradientEv[0] += dEv;
                    gradientLog[0] += dLog;
                    for (var k = 1; k <= number; k++)
                    {
                        gradientEv[k] += dEv * x[k - 1];
                        gradientLog[k] += dLog * x[k - 1];
                    }
                }
                var n = training.Count;
                f.ev[0] -= StepSize * gradientEv[0] / n;
                f.logistic[0] -= StepSize * gradientLog[0] / n;
                for (var k = 1; k <= number; k++)
                {
                    f.ev[k] -= StepSize * (gradientEv[k] / n + L2 * f.ev[k]);
                    f.logistic[k] -= StepSize * (gradientLog[k] / n + L2 * f.logistic[k]);
                }
            }
            return f;
        }
    }

    internal sealed class FrozenCohort
    {
        private readonly Scenario scenario;
        private readonly Dictionary<string, FittedModel> fitted = new();
        private readonly Dictionary<string, double> topQuartile = new();
        internal double PriorEvUsd { get; }
        internal double PriorTarget { get; }

        internal FrozenCohort(Scenario cohort, Candidate[] train)
        {
            scenario = cohort;
            if (train.Length < 50)
                throw new InvalidOperationException("At least 50 training breakouts required in " + scenario.Name);
            PriorEvUsd = train.Average(x => x.ClippedNet);
            PriorTarget = train.Count(x => x.TargetFirst) / (double)train.Length;
            foreach (var name in Methods(cohort).Where(x => x != "BASE"))
            {
                var prepared = train.Select(x =>
                    (Features: Features(x, cohort, name), Candidate: x)).ToArray();
                var m = FittedModel.Train(prepared, FeatureCount(cohort, name));
                fitted.Add(name, m);
                var values = prepared.Select(x => m.Predict(x.Features).ExpectedNetUsd)
                    .OrderBy(x => x).ToArray();
                topQuartile[name] = values[
                    (int)Math.Floor((values.Length - 1) * 0.75)];
            }
        }

        internal Prediction Predict(Candidate c, string method) =>
            method == "BASE"
                ? new(PriorEvUsd, PriorTarget)
                : fitted[method].Predict(Features(c, scenario, method));

        internal double Cutoff(string method) => topQuartile[method];
    }

    private static (Candidate[] Sample, int Early, int None, int Ambiguous)
        Build(IReadOnlyList<Row> rows, Scenario cohort)
    {
        var valid = new List<Candidate>();
        int early = 0, none = 0, ambiguous = 0;
        foreach (var row in rows)
        {
            var observation = Observe(row, cohort);
            switch (observation.Exclusion)
            {
                case Exclusion.None: valid.Add(observation.Candidate!); break;
                case Exclusion.EarlyBreak: early++; break;
                case Exclusion.NoBreak: none++; break;
                case Exclusion.Ambiguous: ambiguous++; break;
                case Exclusion.Incomplete: none++; break;
            }
        }
        return (valid.ToArray(), early, none, ambiguous);
    }

    internal static void Report(
        Row[] older, Row[] recent, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var clock = Stopwatch.StartNew();
        var split = OpeningDirectionResearch.Split(older.Length);
        writer.WriteLine("WINDOW_EV_METHOD,decisions=09:45_09:50_10:00_10:05_ET," +
            "range_minutes=15_or_30,signal_minutes_after_decision=until_10:59_ET," +
            "entry=NEXT_open_after_fully_completed_first_break," +
            "excludes_any_prior_break_when_waiting_5_minutes," +
            "fixed_stop=10_ES_points,fixed_target=15_ES_points,force_exit=12:00_ET," +
            "one_ES_contract,baseline_roundtrip_cost_usd=10," +
            "model=training_only_ridge_EV_and_logistic_target," +
            "training_EV_labels_clipped_to_plus_minus_1500_USD," +
            "trading_PNL_UNCLIPPED=true,epochs=400,l2=0.07," +
            "no_parallelism,no_parameter_search,news_not_tested");
        writer.WriteLine("WINDOW_EV_COHORT,cohort,period,complete_days," +
            "eligible_breakouts,early_break_excluded,no_break_excluded," +
            "ambiguous_break_excluded,avg_opening_width_points," +
            "target_hits,winning_trades,mean_net_usd,avg_MFE_30min_points," +
            "avg_MAE_30min_points");
        writer.WriteLine("WINDOW_EV_MODEL,cohort,period,model,trades," +
            "predicted_avg_ev_usd,realized_avg_net_usd,mae_usd,rmse_usd," +
            "pred_vs_realized_pearson_r,predicted_target_pct,actual_target_pct," +
            "brier,training_baseline_brier,brier_gain_vs_baseline");
        writer.WriteLine("WINDOW_EV_COMPARE,cohort,period,simpler,richer," +
            "matched_trades,rmse_gain_richer,mae_gain_richer,brier_gain_richer");
        writer.WriteLine("WINDOW_EV_FILTER,cohort,period,model,filter," +
            "training_quartile_ev_cutoff_usd,trades,coverage_pct,win_pct," +
            "target_hit_pct,net_usd,max_dd_usd,profit_factor,net_if_20_cost," +
            "net_if_30_cost");
        writer.WriteLine("WINDOW_EV_BINS,cohort,period,model,estimated_ev_bin," +
            "trades,mean_predicted_ev_usd,mean_realized_net_usd," +
            "realized_positive_net_pct,target_first_pct");
        writer.WriteLine("WINDOW_EV_POST5,cohort,period,behavior,trades," +
            "mean_net_usd,win_pct,target_first_pct");
        writer.WriteLine("WINDOW_EV_PAIR15_30,period,matched_days,net_15_usd," +
            "net_30_usd,net_30_minus_15_usd,avg_15_usd,avg_30_usd");
        writer.WriteLine("WINDOW_EV_YEAR,cohort,period,year,model,filter,trades,net_usd");

        var byName = new Dictionary<string, Dictionary<string, Candidate[]>>();
        foreach (var scenario in Scenarios)
        {
            var partitions = new (string Name, Row[] Sessions)[] {
                ("TRAIN", older[..split.TrainEnd]),
                ("VALIDATION", older[split.TrainEnd..split.ValidationEnd]),
                ("HISTORICAL_TEST_ALREADY_SEEN", older[split.ValidationEnd..]),
                ("RECENT_ALREADY_SEEN", recent)
            };
            var cohorts = partitions.ToDictionary(
                x => x.Name, x => Build(x.Sessions, scenario));
            var train = cohorts["TRAIN"].Sample;
            var fit = new FrozenCohort(scenario, train);
            var metrics = new Dictionary<string, Candidate[]>();
            foreach (var (period, data) in partitions)
            {
                var source = cohorts[period];
                var trades = source.Sample;
                metrics[period] = trades;
                writer.WriteLine("WINDOW_EV_COHORT," + scenario.Name + "," +
                    period + "," + data.Sessions.Length + "," + trades.Length +
                    "," + source.Early + "," + source.None + "," + source.Ambiguous +
                    "," + Average(trades.Select(x => x.Width)) +
                    "," + trades.Count(x => x.TargetFirst) +
                    "," + trades.Count(x => x.Profitable) +
                    "," + Average(trades.Select(x => x.Net)) +
                    "," + Average(trades.Select(x => x.Fill.Mfe30Points)) +
                    "," + Average(trades.Select(x => x.Fill.Mae30Points)));
                foreach (var method in Methods(scenario))
                    PrintModel(scenario, period, method, trades, fit, writer);
                for (var j = 2; j < Methods(scenario).Length; j++)
                {
                    var methods = Methods(scenario);
                    PrintCompare(scenario, period, methods[j - 1],
                        methods[j], trades, fit, writer);
                }
                PrintCompare(scenario, period, "BASE", "CORE", trades, fit, writer);
                foreach (var method in new[] { "CORE", Methods(scenario)[^1] }.Distinct())
                {
                    PrintBins(scenario, period, method, trades, fit, writer);
                    PrintFilter(scenario, period, method,
                        "PRED_EV_POSITIVE", trades, fit, writer);
                    PrintFilter(scenario, period, method,
                        "TRAIN_TOP25_AND_POSITIVE", trades, fit, writer);
                }
                PrintFilter(scenario, period, "BASE",
                    "ALL_BREAKS", trades, fit, writer);

                if (scenario.HasPost)
                    foreach (var behavior in new[] {
                        "CONTINUE", "REVERSE", "FLAT", "NO_OPEN_TREND"
                    })
                    {
                        var matching = trades.Where(x => x.PostBehavior == behavior).ToArray();
                        writer.WriteLine("WINDOW_EV_POST5," + scenario.Name +
                            "," + period + "," + behavior + "," + matching.Length +
                            "," + Average(matching.Select(x => x.Net)) +
                            "," + Percentage(matching.Count(x => x.Profitable), matching.Length) +
                            "," + Percentage(matching.Count(x => x.TargetFirst), matching.Length));
                    }
            }
            byName[scenario.Name] = metrics;
        }
        foreach (var period in new[] {
            "TRAIN", "VALIDATION", "HISTORICAL_TEST_ALREADY_SEEN", "RECENT_ALREADY_SEEN"
        })
        {
            var a = byName["ORB15_0945"][period].ToDictionary(x => x.Day);
            var b = byName["ORB30_1000"][period].ToDictionary(x => x.Day);
            var matched = a.Keys.Intersect(b.Keys).ToArray();
            var sumA = matched.Sum(x => a[x].Fill.Net);
            var sumB = matched.Sum(x => b[x].Fill.Net);
            writer.WriteLine("WINDOW_EV_PAIR15_30," + period + "," +
                matched.Length + "," + F(sumA) + "," + F(sumB) +
                "," + F(sumB - sumA) + "," +
                (matched.Length == 0 ? "na" : F(sumA / matched.Length)) +
                "," + (matched.Length == 0 ? "na" : F(sumB / matched.Length)));
        }
        writer.WriteLine("WINDOW_EV_CAVEATS,comparison_15_vs_30_changes_entry_time_and_range," +
            "within_cohort_model_comparisons_are_matched_same_trade," +
            "waiting_5_min_excludes_earlier_breaks_creating_conditional_sample," +
            "stop_first_when_minute_touches_stop_and_target," +
            "MFE_MAE_may_include_candles_after_hypothetical_exit," +
            "probabilities_not_optimized_for_profit," +
            "past_test_periods_not_pristine_unseen," +
            "one_minute_OHLC_not_orderbook_fills," +
            "NO_LIVE_TRADES");
        writer.WriteLine("WINDOW_EV_SECONDS," + F(clock.Elapsed.TotalSeconds));
        writer.WriteLine("WINDOW_EV_COMPLETE");
    }

    private static void PrintModel(Scenario sc, string period,
        string method, Candidate[] sample, FrozenCohort fit, TextWriter writer)
    {
        if (sample.Length == 0) return;
        var expected = new double[sample.Length];
        var realized = new double[sample.Length];
        double rmse = 0, mae = 0, brier = 0, baselineBrier = 0;
        double pTotal = 0;
        for (var j = 0; j < sample.Length; j++)
        {
            var row = sample[j];
            var p = fit.Predict(row, method);
            expected[j] = p.ExpectedNetUsd;
            realized[j] = row.Net;
            var residual = expected[j] - realized[j];
            rmse += residual * residual;
            mae += Math.Abs(residual);
            var target = row.TargetFirst ? 1.0 : 0;
            brier += Math.Pow(p.TargetProbability - target, 2);
            baselineBrier += Math.Pow(fit.PriorTarget - target, 2);
            pTotal += p.TargetProbability;
        }
        var n = sample.Length;
        writer.WriteLine("WINDOW_EV_MODEL," + sc.Name + "," + period +
            "," + method + "," + n + "," +
            F(expected.Average()) + "," + F(realized.Average()) +
            "," + F(mae / n) + "," + F(Math.Sqrt(rmse / n)) +
            "," + F(Pearsons(expected, realized)) +
            "," + F(pTotal * 100 / n) +
            "," + Percentage(sample.Count(x => x.TargetFirst), n) +
            "," + F(brier / n) + "," + F(baselineBrier / n) +
            "," + F((baselineBrier - brier) / n));
    }

    private static void PrintCompare(Scenario sc, string period,
        string simpler, string richer, Candidate[] sample, FrozenCohort fit,
        TextWriter writer)
    {
        if (sample.Length == 0) return;
        double rmseA = 0, rmseB = 0, maeA = 0, maeB = 0, brierA = 0, brierB = 0;
        foreach (var row in sample)
        {
            var a = fit.Predict(row, simpler);
            var b = fit.Predict(row, richer);
            var ta = row.TargetFirst ? 1.0 : 0;
            rmseA += Math.Pow(a.ExpectedNetUsd - row.Net, 2);
            rmseB += Math.Pow(b.ExpectedNetUsd - row.Net, 2);
            maeA += Math.Abs(a.ExpectedNetUsd - row.Net);
            maeB += Math.Abs(b.ExpectedNetUsd - row.Net);
            brierA += Math.Pow(a.TargetProbability - ta, 2);
            brierB += Math.Pow(b.TargetProbability - ta, 2);
        }
        var n = sample.Length;
        writer.WriteLine("WINDOW_EV_COMPARE," + sc.Name + "," + period +
            "," + simpler + "," + richer + "," + n +
            "," + F(Math.Sqrt(rmseA / n) - Math.Sqrt(rmseB / n)) +
            "," + F((maeA - maeB) / n) +
            "," + F((brierA - brierB) / n));
    }

    private static void PrintBins(Scenario sc, string period,
        string method, Candidate[] sample, FrozenCohort fit, TextWriter writer)
    {
        var buckets = new[] {
            (Name: "LE_MINUS_100", Low: double.NegativeInfinity, High: -100.0),
            (Name: "-100_TO_0", Low: -100.0, High: 0.0),
            (Name: "0_TO_100", Low: 0.0, High: 100.0),
            (Name: "GE_100", Low: 100.0, High: double.PositiveInfinity)
        };
        foreach (var b in buckets)
        {
            var matching = sample.Select(x => (X: x, P: fit.Predict(x, method)))
                .Where(x => x.P.ExpectedNetUsd >= b.Low &&
                            x.P.ExpectedNetUsd < b.High).ToArray();
            writer.WriteLine("WINDOW_EV_BINS," + sc.Name + "," + period +
                "," + method + "," + b.Name + "," + matching.Length +
                "," + Average(matching.Select(x => x.P.ExpectedNetUsd)) +
                "," + Average(matching.Select(x => x.X.Net)) +
                "," + Percentage(matching.Count(x => x.X.Profitable), matching.Length) +
                "," + Percentage(matching.Count(x => x.X.TargetFirst), matching.Length));
        }
    }

    private static void PrintFilter(Scenario sc, string period,
        string model, string policy, Candidate[] sample, FrozenCohort fitted,
        TextWriter writer)
    {
        var cutoff = model == "BASE" ? "na" : F(fitted.Cutoff(model));
        var taken = sample.Where(x =>
        {
            if (policy == "ALL_BREAKS") return true;
            var ev = fitted.Predict(x, model).ExpectedNetUsd;
            return ev > 0 && (policy != "TRAIN_TOP25_AND_POSITIVE" ||
                               ev >= fitted.Cutoff(model));
        }).OrderBy(x => x.Fill.EntryAt).ToArray();
        var n = taken.Length;
        var net = taken.Sum(x => x.Fill.Net);
        decimal equity = 0, peak = 0, dd = 0;
        foreach (var x in taken)
        {
            equity += x.Fill.Net;
            peak = Math.Max(peak, equity);
            dd = Math.Max(dd, peak - equity);
        }
        var profit = taken.Sum(x => Math.Max(0m, x.Fill.Net));
        var loss = taken.Sum(x => Math.Max(0m, -x.Fill.Net));
        var pf = loss > 0 ? F((double)(profit / loss)) : n == 0 ? "na" : "infinite";
        writer.WriteLine("WINDOW_EV_FILTER," + sc.Name + "," + period +
            "," + model + "," + policy + "," + cutoff + "," + n +
            "," + Percentage(n, sample.Length) +
            "," + Percentage(taken.Count(x => x.Profitable), n) +
            "," + Percentage(taken.Count(x => x.TargetFirst), n) +
            "," + F(net) + "," + F(dd) + "," + pf +
            "," + F(net - n * 10m) + "," + F(net - n * 20m));
        if (policy is "ALL_BREAKS" or "TRAIN_TOP25_AND_POSITIVE")
            foreach (var yr in taken.GroupBy(x => x.Day.Year))
                writer.WriteLine("WINDOW_EV_YEAR," + sc.Name + "," +
                    period + "," + yr.Key + "," + model + "," + policy +
                    "," + yr.Count() + "," + F(yr.Sum(x => x.Fill.Net)));
    }

    private static string F(double n) =>
        double.IsNaN(n) ? "na" : n.ToString("0.0000", Ci);
    private static string F(decimal n) => n.ToString("0.00", Ci);
    private static string Average(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? "na" : F(materialized.Average());
    }
    private static string Percentage(int a, int b) =>
        b == 0 ? "na" : F(a * 100.0 / b);

    public static async Task<int> RunAsync(
        string? sql, string symbol, string timezone)
    {
        var timer = Stopwatch.StartNew();
        Console.WriteLine("=== ES 15/30-MINUTE WINDOW + POST-WINDOW EV RESEARCH ===");
        Console.WriteLine("Fast sequential models, fixed 10/15-point trade, training-only freezes.");
        var clock = Stopwatch.StartNew();
        var oldData = await SqlMarketDataLoader.LoadAsync(
            sql, symbol, timezone, tableName: "MES_DATA_2020_2025");
        var older = OpeningDirectionResearch.Extract(oldData.ActiveBars);
        Console.WriteLine("WINDOW_EV_SOURCE,historical,sql_and_extract_seconds=" +
            F(clock.Elapsed.TotalSeconds) + ",complete_sessions=" + older.Length);
        oldData = null!;
        GC.Collect();
        clock.Restart();
        var recentData = await SqlMarketDataLoader.LoadAsync(
            sql, symbol, timezone, tableName: "MES_DATA");
        var recent = OpeningDirectionResearch.Extract(recentData.ActiveBars);
        Console.WriteLine("WINDOW_EV_SOURCE,recent,sql_and_extract_seconds=" +
            F(clock.Elapsed.TotalSeconds) + ",complete_sessions=" + recent.Length);
        if (older.Length < 100 || recent.Length == 0 ||
            recent[0].Day <= older[^1].Day)
            throw new InvalidOperationException("Need 100+ complete old sessions and disjoint newer SQL data.");
        Report(older, recent);
        Console.WriteLine("WINDOW_EV_TOTAL_SECONDS," + F(timer.Elapsed.TotalSeconds));
        Console.WriteLine("Research only: no order submission, no SQL modifications.");
        return 0;
    }
}
