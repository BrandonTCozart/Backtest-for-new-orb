using System.Diagnostics;
using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

// Fast, single-threaded directional research. No strategy sweeps, no trading
// simulation, no live orders, and no fitted information from the test windows.
// Features available at 09:30 or 09:45 ET are kept separate from their targets.
public static class OpeningDirectionResearch
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const int OpeningMinutes = 15;
    private const int RthMinutes = 390;
    private const double FlatPoints = 1.0; // predeclared 4-tick dead band
    private const double PriorWeight = 20.0; // empirical-Bayes shrinkage
    private const int MinimumCellSupport = 15;

    internal enum Outcome { Down = -1, Flat = 0, Up = 1, Ambiguous = 2, None = 3 }
    internal enum PremarketWindow { Overnight, FourAm, EightAm }
    internal enum Target { Opening15, FirstBreakBy11, RestOfDay, FullRth }
    internal sealed record PreMove(bool Available, double Points, int Sign);
    internal sealed record SessionRow(
        DateOnly Day, string Contract, double[] Shape, double RangePoints,
        PreMove Overnight, PreMove FourAm, PreMove EightAm, double GapPoints,
        bool HasGap, Outcome Opening15, Outcome FirstBreakBy11,
        Outcome RestOfDay, Outcome FullRth,
        double Opening15Points, double RestOfDayPoints,
        double FullRthPoints,
        decimal OpeningHigh, decimal OpeningLow, decimal OpeningClose,
        int FirstBreakBarIndex, PriceBar[] Rth);

    internal sealed record Partition(int TrainEnd, int ValidationEnd, int HistoricalCount);
    internal sealed record Forecast(
        DateOnly Day, string Period, string Model, Target Target,
        Outcome Actual, double ProbabilityUp, bool CanForecast);

    internal sealed class ShapeFit
    {
        internal required double[][] Centers { get; init; }

        internal int Assign(double[] path)
        {
            var selected = 0;
            var distance = double.PositiveInfinity;
            for (var k = 0; k < Centers.Length; k++)
            {
                double sum = 0;
                for (var i = 0; i < OpeningMinutes; i++)
                {
                    var d = path[i] - Centers[k][i];
                    sum += d * d;
                }
                if (sum < distance) { distance = sum; selected = k; }
            }
            return selected;
        }

        internal static ShapeFit Train(IReadOnlyList<SessionRow> training, int groups)
        {
            if (training.Count < groups * 10)
                throw new ArgumentException("Need at least ten training sessions per shape group.");
            // Shapes use close-vs-open / complete opening range, all 15 minutes.
            // The group model sees no future bars and no premarket variables.
            var centers = new double[groups][];
            centers[0] = (double[])training[0].Shape.Clone();
            for (var k = 1; k < groups; k++)
            {
                SessionRow? farthest = null;
                double max = double.NegativeInfinity;
                foreach (var row in training)
                {
                    var nearest = double.PositiveInfinity;
                    for (var j = 0; j < k; j++)
                        nearest = Math.Min(nearest, Squared(row.Shape, centers[j]));
                    if (nearest > max) { max = nearest; farthest = row; }
                }
                centers[k] = (double[])farthest!.Shape.Clone();
            }

            var assignment = Enumerable.Repeat(-1, training.Count).ToArray();
            for (var pass = 0; pass < 60; pass++)
            {
                var counts = new int[groups];
                var sums = new double[groups][];
                for (var k = 0; k < groups; k++) sums[k] = new double[OpeningMinutes];
                var changed = false;
                for (var r = 0; r < training.Count; r++)
                {
                    var nearest = 0;
                    var best = Squared(training[r].Shape, centers[0]);
                    for (var k = 1; k < groups; k++)
                    {
                        var distance = Squared(training[r].Shape, centers[k]);
                        if (distance < best) { nearest = k; best = distance; }
                    }
                    if (assignment[r] != nearest) changed = true;
                    assignment[r] = nearest;
                    counts[nearest]++;
                    for (var i = 0; i < OpeningMinutes; i++)
                        sums[nearest][i] += training[r].Shape[i];
                }
                if (!changed) break;
                for (var k = 0; k < groups; k++)
                {
                    // Keep an empty group's last centroid; do not fabricate
                    // one by inspecting validation or held-out test examples.
                    if (counts[k] == 0) continue;
                    for (var i = 0; i < OpeningMinutes; i++)
                        centers[k][i] = sums[k][i] / counts[k];
                }
            }
            return new ShapeFit { Centers = centers };
        }

        private static double Squared(double[] a, double[] b)
        {
            double result = 0;
            for (var i = 0; i < OpeningMinutes; i++)
            {
                var delta = a[i] - b[i];
                result += delta * delta;
            }
            return result;
        }
    }

    internal sealed record Count(int N, int Up)
    {
        internal double Shrink(double prior, double weight = PriorWeight) =>
            (Up + weight * prior) / (N + weight);
    }

    internal sealed class FrozenProbabilities
    {
        internal readonly ShapeFit Shape;
        internal readonly double BaselineUp;
        internal readonly Dictionary<int, Count> ShapeCells = new();
        internal readonly Dictionary<int, Count> PremarketCells = new();
        internal readonly Dictionary<(int Shape, int Pre), Count> CombinedCells = new();
        internal readonly Target Target;

        internal FrozenProbabilities(
            IReadOnlyList<SessionRow> training, ShapeFit shape, Target target)
        {
            Shape = shape;
            Target = target;
            var valid = training.Where(x => Binary(TargetOf(x, target))).ToArray();
            BaselineUp = valid.Length == 0
                ? 0.5 : valid.Count(x => TargetOf(x, target) == Outcome.Up) / (double)valid.Length;
            foreach (var row in valid)
            {
                var s = shape.Assign(row.Shape);
                Append(ShapeCells, s, TargetOf(row, target));
                if (row.FourAm.Available)
                {
                    Append(PremarketCells, row.FourAm.Sign, TargetOf(row, target));
                    Append(CombinedCells, (s, row.FourAm.Sign), TargetOf(row, target));
                }
            }
        }

        internal double Probability(SessionRow row, string model)
        {
            var s = Shape.Assign(row.Shape);
            var shapeP = ShapeCells.TryGetValue(s, out var sc)
                ? sc.Shrink(BaselineUp) : BaselineUp;
            return model switch
            {
                "BASE" => BaselineUp,
                "SHAPE" => shapeP,
                "PRE4" => row.FourAm.Available &&
                    PremarketCells.TryGetValue(row.FourAm.Sign, out var pc)
                    ? pc.Shrink(BaselineUp) : BaselineUp,
                "SHAPE_PRE4" => row.FourAm.Available &&
                    CombinedCells.TryGetValue((s, row.FourAm.Sign), out var joint) &&
                    joint.N >= MinimumCellSupport
                    ? joint.Shrink(shapeP) : shapeP,
                _ => throw new ArgumentException("Unknown forecast model: " + model)
            };
        }

        private static void Append<T>(Dictionary<T, Count> counts, T key,
            Outcome result) where T : notnull
        {
            counts.TryGetValue(key, out var prior);
            counts[key] = new Count((prior?.N ?? 0) + 1,
                (prior?.Up ?? 0) + (result == Outcome.Up ? 1 : 0));
        }
    }

    internal static bool Binary(Outcome x) => x is Outcome.Up or Outcome.Down;
    internal static Outcome TargetOf(SessionRow row, Target target) => target switch
    {
        Target.Opening15 => row.Opening15,
        Target.FirstBreakBy11 => row.FirstBreakBy11,
        Target.RestOfDay => row.RestOfDay,
        Target.FullRth => row.FullRth,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    internal static Partition Split(int count)
    {
        if (count < 50) throw new ArgumentException("Need 50+ historical sessions.");
        // 60% train, 20% validation, 20% previously-inspected historical test.
        return new Partition(count * 3 / 5, count * 4 / 5, count);
    }

    // Locate bars by UTC timestamp with binary search, instead of building
    // 1.7M+ contract/time dictionary entries, indicators, or overnight regimes.
    private static int LowerBound(IReadOnlyList<PriceBar> bars, DateTimeOffset time)
    {
        var min = 0; var max = bars.Count;
        while (min < max)
        {
            var mid = min + (max - min) / 2;
            if (bars[mid].T < time) min = mid + 1;
            else max = mid;
        }
        return min;
    }

    private static bool HasWindow(
        IReadOnlyList<PriceBar> bars, DateOnly day, TimeOnly start,
        TimeOnly end, string contract, out decimal first, out decimal last)
    {
        first = last = 0m;
        var from = MarketTime.ToUtc(day, start);
        var through = MarketTime.ToUtc(day, end);
        var minutes = (int)(through - from).TotalMinutes + 1;
        var idx = LowerBound(bars, from);
        if (idx + minutes > bars.Count || minutes < 1) return false;
        for (var i = 0; i < minutes; i++)
        {
            var bar = bars[idx + i];
            if (bar.T != from.AddMinutes(i) ||
                !string.Equals(contract, bar.Contract, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        first = bars[idx].C.O;
        last = bars[idx + minutes - 1].C.C;
        return true;
    }

    private static PreMove Premarket(
        IReadOnlyList<PriceBar> bars, DateOnly startDate, TimeOnly start,
        DateOnly tradeDate, string contract)
    {
        var from = MarketTime.ToUtc(startDate, start);
        var to = MarketTime.ToUtc(tradeDate, new TimeOnly(9, 29));
        var firstIndex = LowerBound(bars, from);
        var length = (int)(to - from).TotalMinutes + 1;
        if (firstIndex + length > bars.Count || length <= 0)
            return new PreMove(false, 0, 0);
        // Require the full contiguous same-contract window, not just two end
        // prices that might bridge a missing candle or a contract roll.
        for (var i = 0; i < length; i++)
        {
            var bar = bars[firstIndex + i];
            if (bar.T != from.AddMinutes(i) ||
                !string.Equals(bar.Contract, contract, StringComparison.OrdinalIgnoreCase))
                return new PreMove(false, 0, 0);
        }
        var delta = (double)(bars[firstIndex + length - 1].C.C -
                             bars[firstIndex].C.O);
        return new PreMove(true, delta, delta > FlatPoints ? 1 :
            delta < -FlatPoints ? -1 : 0);
    }

    internal static SessionRow[] Extract(IReadOnlyList<PriceBar> bars)
    {
        if (bars.Count == 0) return [];
        for (var i = 1; i < bars.Count; i++)
            if (bars[i].T <= bars[i - 1].T)
                throw new ArgumentException("Active bars must be strictly UTC-ordered.");
        var firstDay = DateOnly.FromDateTime(MarketTime.ToEastern(bars[0].T).Date);
        var lastDay = DateOnly.FromDateTime(MarketTime.ToEastern(bars[^1].T).Date);
        var output = new List<SessionRow>();
        DateOnly? previousDay = null;
        string? previousContract = null;
        decimal previousClose = 0;
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var rthStart = MarketTime.ToUtc(day, new TimeOnly(9, 30));
            var firstIndex = LowerBound(bars, rthStart);
            if (firstIndex + RthMinutes > bars.Count ||
                bars[firstIndex].T != rthStart) continue;
            var contract = bars[firstIndex].Contract;
            var complete = true;
            for (var i = 0; i < RthMinutes; i++)
                if (bars[firstIndex + i].T != rthStart.AddMinutes(i) ||
                    !string.Equals(contract, bars[firstIndex + i].Contract,
                        StringComparison.OrdinalIgnoreCase))
                {
                    complete = false;
                    break;
                }
            if (!complete) continue;

            var firstOpen = bars[firstIndex].C.O;
            var lastOpenClose = bars[firstIndex + 14].C.C;
            var dayClose = bars[firstIndex + 389].C.C;
            decimal high = decimal.MinValue, low = decimal.MaxValue;
            for (var i = 0; i < OpeningMinutes; i++)
            {
                var c = bars[firstIndex + i].C;
                high = Math.Max(high, c.H);
                low = Math.Min(low, c.L);
            }
            var openingWidth = Math.Max(0.25m, high - low);
            var shape = new double[OpeningMinutes];
            for (var i = 0; i < OpeningMinutes; i++)
                shape[i] = (double)((bars[firstIndex + i].C.C - firstOpen) / openingWidth);

            // First confirmed crossing AFTER the range exists (09:45..10:59).
            // Same-minute break of BOTH boundaries is unresolvable with OHLC.
            var breakout = Outcome.None;
            var firstBreakIndex = -1;
            for (var i = OpeningMinutes; i < 90; i++)
            {
                var c = bars[firstIndex + i].C;
                var up = c.H > high;
                var down = c.L < low;
                if (up && down) { breakout = Outcome.Ambiguous; firstBreakIndex = i; break; }
                if (up) { breakout = Outcome.Up; firstBreakIndex = i; break; }
                if (down) { breakout = Outcome.Down; firstBreakIndex = i; break; }
            }

            var night = Premarket(bars, day.AddDays(-1),
                new TimeOnly(18, 0), day, contract);
            var four = Premarket(bars, day,
                new TimeOnly(4, 0), day, contract);
            var eight = Premarket(bars, day,
                new TimeOnly(8, 0), day, contract);
            var hasPrior = previousDay.HasValue &&
                day.DayNumber - previousDay.Value.DayNumber <= 4 &&
                string.Equals(previousContract, contract,
                    StringComparison.OrdinalIgnoreCase);
            output.Add(new SessionRow(day, contract, shape,
                (double)openingWidth, night, four, eight,
                hasPrior ? (double)(firstOpen - previousClose) : 0,
                hasPrior,
                Direction(lastOpenClose - firstOpen),
                breakout,
                Direction(dayClose - lastOpenClose),
                Direction(dayClose - firstOpen),
                (double)(lastOpenClose - firstOpen),
                (double)(dayClose - lastOpenClose),
                (double)(dayClose - firstOpen),
                high, low, lastOpenClose, firstBreakIndex,
                // Only 390 references per session; no heavy indicators,
                // caching or per-strategy lookbacks needed for execution audit.
                bars.Skip(firstIndex).Take(RthMinutes).ToArray()));
            previousDay = day;
            previousContract = contract;
            previousClose = dayClose;
        }
        return output.ToArray();
    }

    private static Outcome Direction(decimal change) =>
        change > 0.25m ? Outcome.Up :
        change < -0.25m ? Outcome.Down : Outcome.Flat;

    internal static void Print(
        SessionRow[] historical, SessionRow[] recent, int groups,
        TextWriter? writer = null)
    {
        writer ??= Console.Out;
        var sw = Stopwatch.StartNew();
        var split = Split(historical.Length);
        var frozen = ShapeFit.Train(historical[..split.TrainEnd], groups);
        writer.WriteLine("DIRECTION_RESEARCH_METHOD,decision=09:45_ET,shape=15_normalized_closes," +
            "window=09:30-09:44_ET,outcomes=first_09:45-11:00_break_and_09:45-16:00_direction");
        writer.WriteLine("DIRECTION_RESEARCH_SPLIT,period,days,start,end");
        PeriodDates("TRAIN", historical, 0, split.TrainEnd);
        PeriodDates("VALIDATION", historical, split.TrainEnd, split.ValidationEnd);
        PeriodDates("HISTORICAL_TEST_ALREADY_SEEN", historical, split.ValidationEnd, split.HistoricalCount);
        PeriodDates("RECENT_ALREADY_SEEN", recent, 0, recent.Length);
        writer.WriteLine("DIRECTION_RESEARCH_SHAPE,cluster,train_days,avg_opening_range_points," +
            "centroid_close_path_normalized_by_opening_width");
        for (var group = 0; group < groups; group++)
        {
            var matching = historical.Take(split.TrainEnd)
                .Where(row => frozen.Assign(row.Shape) == group).ToArray();
            writer.WriteLine("DIRECTION_RESEARCH_SHAPE," + group + "," + matching.Length + "," +
                F(matching.Length == 0 ? 0 : matching.Average(x => x.RangePoints)) +
                "," + string.Join(";", frozen.Centers[group].Select(F)));
        }
        writer.WriteLine("DIRECTION_RESEARCH_PREMARKET,period,window,signal,days," +
            "opening_up,opening_down,break_up,break_down,break_none_or_ambiguous," +
            "rest_day_up,rest_day_down,full_day_up,full_day_down");
        foreach (var (period, sample) in Sections(historical, recent, split))
            foreach (var window in Enum.GetValues<PremarketWindow>())
                foreach (var sign in new[] { -1, 0, 1 })
                {
                    var group = sample.Where(row =>
                    {
                        var pm = Pre(row, window);
                        return pm.Available && pm.Sign == sign;
                    }).ToArray();
                    writer.WriteLine("DIRECTION_RESEARCH_PREMARKET," + period + "," +
                        window + "," + SignName(sign) + "," + group.Length +
                        "," + CountOutcome(group, Target.Opening15, Outcome.Up) +
                        "," + CountOutcome(group, Target.Opening15, Outcome.Down) +
                        "," + CountOutcome(group, Target.FirstBreakBy11, Outcome.Up) +
                        "," + CountOutcome(group, Target.FirstBreakBy11, Outcome.Down) +
                        "," + group.Count(x => !Binary(x.FirstBreakBy11)) +
                        "," + CountOutcome(group, Target.RestOfDay, Outcome.Up) +
                        "," + CountOutcome(group, Target.RestOfDay, Outcome.Down) +
                        "," + CountOutcome(group, Target.FullRth, Outcome.Up) +
                        "," + CountOutcome(group, Target.FullRth, Outcome.Down));
                }
        writer.WriteLine("DIRECTION_RESEARCH_CORRELATION,period,premarket_window," +
            "outcome,paired_days,pearson_r,sign_agreement_pct");
        foreach (var (period, sample) in Sections(historical, recent, split))
            foreach (var window in Enum.GetValues<PremarketWindow>())
                foreach (var target in new[] {
                    Target.Opening15, Target.FirstBreakBy11,
                    Target.RestOfDay, Target.FullRth })
                    PrintCorrelation(period, sample, window, target);

        writer.WriteLine("DIRECTION_RESEARCH_CONDITIONAL,period,shape,pre4,days," +
            "break_up,break_down,rest_day_up,rest_day_down");
        foreach (var (period, sample) in Sections(historical, recent, split))
        {
            for (var shape = 0; shape < groups; shape++)
                foreach (var pre in new[] { -1, 0, 1 })
                {
                    var matching = sample.Where(x =>
                        frozen.Assign(x.Shape) == shape && x.FourAm.Available &&
                        x.FourAm.Sign == pre).ToArray();
                    writer.WriteLine("DIRECTION_RESEARCH_CONDITIONAL," + period + "," +
                        shape + "," + SignName(pre) + "," + matching.Length + "," +
                        CountOutcome(matching, Target.FirstBreakBy11, Outcome.Up) + "," +
                        CountOutcome(matching, Target.FirstBreakBy11, Outcome.Down) + "," +
                        CountOutcome(matching, Target.RestOfDay, Outcome.Up) + "," +
                        CountOutcome(matching, Target.RestOfDay, Outcome.Down));
                }
        }

        writer.WriteLine("DIRECTION_RESEARCH_FORECAST,period,target,model,eligible_binary," +
            "prediction_days,coverage_pct,brier,accuracy_pct,high_confidence_days," +
            "high_confidence_accuracy_pct,baseline_up_pct");
        foreach (var target in new[] { Target.FirstBreakBy11, Target.RestOfDay })
        {
            var model = new FrozenProbabilities(
                historical[..split.TrainEnd], frozen, target);
            foreach (var (period, sample) in Sections(historical, recent, split))
            {
                // TRAIN is shown for diagnostic only; performance cannot be
                // called out-of-sample because TRAIN estimated the probabilities.
                foreach (var method in new[] { "BASE", "PRE4", "SHAPE", "SHAPE_PRE4" })
                    PrintForecast(period, sample, target, method, model);
            }
        }

        writer.WriteLine("DIRECTION_RESEARCH_LIMITS,premarket_missing=excluded_per_window," +
            "close_direction_deadband=1_ES_tick,pre_move_deadband=1_ES_point," +
            "shape_fitted=historical_train_only,probabilities_fitted=historical_train_only," +
            "joint_min_training_support=" + MinimumCellSupport +
            ",probability_shrinkage=" + F(PriorWeight) +
            ",fills_or_trading_PNL=NOT_TESTED,news=NOT_TESTED");
        writer.WriteLine("RESEARCH CAUTION: Pre-09:30 forecasts can use premarket features " +
            "only. Shape forecasts require all 15 completed 09:30-09:44 bars, " +
            "and apply at 09:45. Intrabar first-break ties are ambiguous. " +
            "The newest SQL interval was previously consulted by this project, " +
            "so it is a chronological replay, not a pristine new holdout. " +
            "A positive directional correlation is NOT a profitable strategy.");
        writer.WriteLine("DIRECTION_RESEARCH_REPORT_SECONDS," + F(sw.Elapsed.TotalSeconds));

        void PrintCorrelation(string period, SessionRow[] sample,
            PremarketWindow window, Target target)
        {
            var n = 0; var signAgree = 0;
            double sumX = 0, sumY = 0, xx = 0, yy = 0, xy = 0;
            foreach (var row in sample)
            {
                var pm = Pre(row, window);
                if (!pm.Available) continue;
                var outcome = TargetOf(row, target);
                if (!Binary(outcome)) continue; // exclude ambiguous/none/flat
                var x = pm.Points;
                var y = target switch {
                    Target.Opening15 => row.Opening15Points,
                    Target.RestOfDay => row.RestOfDayPoints,
                    Target.FullRth => row.FullRthPoints,
                    Target.FirstBreakBy11 => outcome == Outcome.Up ? 1 : -1,
                    _ => 0
                };
                n++;
                sumX += x; sumY += y;
                xx += x * x; yy += y * y; xy += x * y;
                if (pm.Sign != 0 && pm.Sign == (int)outcome) signAgree++;
            }
            var denom = (n * xx - sumX * sumX) *
                        (n * yy - sumY * sumY);
            var r = n >= 3 && denom > 1e-9
                ? F((n * xy - sumX * sumY) / Math.Sqrt(denom))
                : "na";
            writer.WriteLine("DIRECTION_RESEARCH_CORRELATION," +
                period + "," + window + "," + target + "," + n +
                "," + r + "," + Percent(signAgree, n));
        }

        void PeriodDates(string name, IReadOnlyList<SessionRow> r, int a, int b)
        {
            writer.WriteLine("DIRECTION_RESEARCH_SPLIT," + name + "," + (b - a) +
                "," + (b > a ? Date(r[a].Day) : "na") +
                "," + (b > a ? Date(r[b - 1].Day) : "na"));
        }

        void PrintForecast(string period, SessionRow[] sample, Target target,
            string method, FrozenProbabilities fitted)
        {
            var valid = 0;
            var predicted = 0;
            var correct = 0;
            var confident = 0;
            var confidentCorrect = 0;
            double brier = 0;
            foreach (var row in sample)
            {
                var truth = TargetOf(row, target);
                if (!Binary(truth)) continue;
                valid++;
                // No silent missing-data imputation for premarket-based rules.
                if ((method is "PRE4" or "SHAPE_PRE4") && !row.FourAm.Available)
                    continue;
                var p = fitted.Probability(row, method);
                var actual = truth == Outcome.Up ? 1.0 : 0;
                brier += (p - actual) * (p - actual);
                predicted++;
                var predictedSide = p >= 0.5 ? Outcome.Up : Outcome.Down;
                if (predictedSide == truth) correct++;
                if (p >= 0.6 || p <= 0.4)
                {
                    confident++;
                    if (predictedSide == truth) confidentCorrect++;
                }
            }
            writer.WriteLine("DIRECTION_RESEARCH_FORECAST," + period + "," +
                target + "," + method + "," + valid + "," + predicted +
                "," + Percent(predicted, valid) + "," +
                (predicted == 0 ? "na" : F(brier / predicted)) + "," +
                Percent(correct, predicted) + "," + confident + "," +
                Percent(confidentCorrect, confident) + "," +
                F(fitted.BaselineUp * 100));
        }
    }

    private static IEnumerable<(string Name, SessionRow[] Rows)> Sections(
        SessionRow[] historical, SessionRow[] recent, Partition split)
    {
        yield return ("TRAIN", historical[..split.TrainEnd]);
        yield return ("VALIDATION", historical[split.TrainEnd..split.ValidationEnd]);
        yield return ("HISTORICAL_TEST_ALREADY_SEEN", historical[split.ValidationEnd..]);
        yield return ("RECENT_ALREADY_SEEN", recent);
    }

    private static PreMove Pre(SessionRow x, PremarketWindow w) => w switch
    {
        PremarketWindow.Overnight => x.Overnight,
        PremarketWindow.FourAm => x.FourAm,
        PremarketWindow.EightAm => x.EightAm,
        _ => throw new ArgumentOutOfRangeException(nameof(w))
    };
    private static int CountOutcome(SessionRow[] x, Target t, Outcome o) =>
        x.Count(row => TargetOf(row, t) == o);
    private static string SignName(int sign) =>
        sign < 0 ? "DOWN" : sign > 0 ? "UP" : "FLAT";
    private static string F(double x) => x.ToString("0.0000", Ci);
    private static string Percent(int n, int d) =>
        d == 0 ? "na" : F(n * 100.0 / d);
    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", Ci);

    public static async Task<int> RunAsync(string? connectionString,
        string symbol, string timeZone, int shapeGroups)
    {
        if (shapeGroups is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(shapeGroups));
        var total = Stopwatch.StartNew();
        Console.WriteLine("=== ES SHAPE + PREMARKET DIRECTION RESEARCH (READ ONLY) ===");
        Console.WriteLine("Single-threaded, fixed-K shape discovery, no ORB grid search.");
        var sqlClock = Stopwatch.StartNew();
        var historical = await SqlMarketDataLoader.LoadAsync(connectionString,
            symbol, timeZone, tableName: "MES_DATA_2020_2025");
        Console.WriteLine("DIRECTION_RESEARCH_SQL_SECONDS,historical," +
            F(sqlClock.Elapsed.TotalSeconds));
        var historicalRows = Extract(historical.ActiveBars);
        Console.WriteLine("DIRECTION_RESEARCH_EXTRACT,historical,days=" +
            historicalRows.Length + ",premarket_04_coverage=" +
            historicalRows.Count(x => x.FourAm.Available));
        // Explicitly release large SQL bar lists before reading the second table.
        // No full-session indicators, reverse caches, optimization grid or P&L.
        historical = null!;
        GC.Collect();
        sqlClock.Restart();
        var recent = await SqlMarketDataLoader.LoadAsync(connectionString,
            symbol, timeZone, tableName: "MES_DATA");
        Console.WriteLine("DIRECTION_RESEARCH_SQL_SECONDS,recent," +
            F(sqlClock.Elapsed.TotalSeconds));
        var recentRows = Extract(recent.ActiveBars);
        Console.WriteLine("DIRECTION_RESEARCH_EXTRACT,recent,days=" +
            recentRows.Length + ",premarket_04_coverage=" +
            recentRows.Count(x => x.FourAm.Available));
        if (historicalRows.Length < 50)
            throw new InvalidOperationException("Need 50+ complete historical RTH sessions.");
        if (recentRows.Length == 0 ||
            recentRows[0].Day <= historicalRows[^1].Day)
            throw new InvalidOperationException(
                "Recent SQL must start strictly after historical SQL " +
                "to prevent double-counted observations.");
        Print(historicalRows, recentRows, shapeGroups);
        OpeningBreakoutValidation.Report(historicalRows, recentRows, shapeGroups);
        Console.WriteLine("DIRECTION_RESEARCH_TOTAL_SECONDS," +
            F(total.Elapsed.TotalSeconds));
        Console.WriteLine("Directional research finished: no trades, no SQL writes.");
        return 0;
    }
}
