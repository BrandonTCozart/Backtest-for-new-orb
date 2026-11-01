using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

public record PriceBar(DateTimeOffset T, string Contract, Candle C);
public record TradeResult(
    DateOnly Day, string Contract, TradeDirection Direction,
    DateTimeOffset SignalAt, DateTimeOffset EnteredAt, decimal Entry, decimal FirstStop,
    DateTimeOffset ExitedAt, decimal Exit, string ExitReason,
    decimal MfePoints, decimal FavorablePrice, decimal RealizedPoints, int FinalStage);

public static class BacktestRunner
{
    const decimal PointValue = 50m;
    const decimal CostAllowance = 10m;
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static readonly TimeSpan OrbEnd = new(9,45,0);
    static readonly TimeSpan SessionEnd = new(16,0,0);

    public static int Run()
    {
        Console.WriteLine("ORB first-trade backtest: Maximum Favorable Excursion (MFE)");
        Console.WriteLine("Local CSV only; no download, Topstep login or orders.");
        var root = FindRoot();
        var dir = Path.Combine(root, "data");
        var files = new[] { "es_1m_3months.csv", "es_1m_3months_INCOMPLETE.csv" }
            .Select(n => Path.Combine(dir, n)).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine("No ES candles found. Put es_1m_3months.csv or es_1m_3months_INCOMPLETE.csv in: " + dir);
            return 1;
        }

        Console.WriteLine("Reading: " + files[0]);
        if (Path.GetFileName(files[0]).Contains("INCOMPLETE", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("WARNING: This is partial downloaded history. Only available sessions are tested.");
        var input = Load(files[0]);
        if (input.Count == 0) { Console.Error.WriteLine("CSV contains no candles."); return 1; }

        var strategy = new OrbStrategy();
        var trades = new List<TradeResult>();
        var skips = new List<(DateOnly Day, string Why)>();
        var sessions = input.Where(x =>
        {
            var et = MarketTime.ToEastern(x.T);
            return et.TimeOfDay >= new TimeSpan(9,30,0) && et.TimeOfDay < SessionEnd &&
                   et.DayOfWeek != DayOfWeek.Saturday && et.DayOfWeek != DayOfWeek.Sunday;
        }).GroupBy(x => DateOnly.FromDateTime(MarketTime.ToEastern(x.T).Date)).OrderBy(x => x.Key);

        foreach (var session in sessions)
        {
            var day = session.Key;
            var candles = session.OrderBy(x => x.T).ToArray();
            var result = Simulate(strategy, input, candles, day, out var reason);
            if (result is null)
            {
                skips.Add((day, reason));
                Console.WriteLine(day.ToString("yyyy-MM-dd", CI) + ": skipped (" + reason + ")");
            }
            else
            {
                trades.Add(result);
                Console.WriteLine(day.ToString("yyyy-MM-dd", CI) + ": " + result.Direction +
                    ", MFE " + result.MfePoints.ToString("0.00", CI) + " pts, exit=" + result.ExitReason);
            }
        }

        Directory.CreateDirectory(dir);
        var tradesFile = Path.Combine(dir, "backtest_first_trade_mfe.csv");
        var skipsFile = Path.Combine(dir, "backtest_skipped_days.csv");
        File.WriteAllLines(tradesFile, new [] {
            "date_et,contract,direction,signal_time_et,entry_time_et,entry_price,initial_stop,exit_time_et,exit_price,exit_reason,mfe_points,mfe_dollars,favorable_extreme,realized_points,realized_dollars,final_stage"
        }.Concat(trades.Select(t => string.Join(",",
            t.Day.ToString("yyyy-MM-dd", CI), t.Contract, t.Direction,
            FormatEt(t.SignalAt), FormatEt(t.EnteredAt), F(t.Entry), F(t.FirstStop),
            FormatEt(t.ExitedAt), F(t.Exit), t.ExitReason, F(t.MfePoints),
            F(t.MfePoints * PointValue), F(t.FavorablePrice), F(t.RealizedPoints),
            F(t.RealizedPoints * PointValue), t.FinalStage.ToString(CI)))));
        File.WriteAllLines(skipsFile, new [] { "date_et,reason" }.Concat(skips.Select(s =>
            s.Day.ToString("yyyy-MM-dd", CI) + ",\"" + s.Why.Replace("\"", "\"\"") + "\"")));

        Console.WriteLine();
        Console.WriteLine("Candle range (UTC): " + input[0].T.ToString("yyyy-MM-dd", CI) +
            " to " + input[^1].T.ToString("yyyy-MM-dd", CI));
        Console.WriteLine("Completed first trades: " + trades.Count + "; skipped sessions: " + skips.Count);
        if (trades.Count == 0)
        {
            Console.WriteLine("No completed trades; see " + skipsFile);
            return 2;
        }
        var m = trades.Select(t => t.MfePoints).OrderBy(x => x).ToArray();
        var avg = m.Average();
        var median = m.Length % 2 == 1 ? m[m.Length/2] : (m[m.Length/2-1] + m[m.Length/2]) / 2m;
        Console.WriteLine("AVERAGE MAXIMUM FAVORABLE MOVE: " + F(avg) + " ES points, $" +
            (avg * PointValue).ToString("N2", CI) + " for one ES contract");
        Console.WriteLine("Median MFE: " + F(median) + " points; range: " + F(m[0]) + "–" + F(m[^1]));
        foreach (var side in new[] { TradeDirection.Long, TradeDirection.Short })
        {
            var group = trades.Where(t => t.Direction == side).ToArray();
            if (group.Length > 0)
                Console.WriteLine(side + ": " + group.Length + " trades, average MFE " +
                    F(group.Average(t => t.MfePoints)) + " points");
        }
        Console.WriteLine("Per-trade results: " + tradesFile);
        Console.WriteLine("Skipped sessions: " + skipsFile);
        Console.WriteLine("Method: next-minute open entry, 1-minute candle extremes for MFE,");
        Console.WriteLine("old stop checked before favorable wick in ambiguous stop-hit minutes,");
        Console.WriteLine("and stage changes at candle close (not tick-perfect).");
        return 0;
    }

    static TradeResult? Simulate(OrbStrategy logic, List<PriceBar> all,
        PriceBar[] bars, DateOnly day, out string why)
    {
        why = "no breakout";
        if (bars.Select(x => x.Contract).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            why = "multiple futures contracts in regular session";
            return null;
        }
        var range = logic.GetOpeningRange(bars.Select(x => x.C), day);
        if (range is null)
        {
            why = "missing 9:30–9:44 ET opening range candle(s)";
            return null;
        }

        var signal = -1;
        TradeDirection? side = null;
        for (var i = 0; i < bars.Length; i++)
        {
            var et = MarketTime.ToEastern(bars[i].T).TimeOfDay;
            if (et < OrbEnd || et >= new TimeSpan(15,59,0)) continue;
            side = logic.GetBreakout(bars[i].C, range);
            if (side is not null) { signal = i; break; }
        }
        if (signal == -1 || side is null)
        {
            why = "no candle closing beyond opening range before 3:59 PM";
            return null;
        }
        if (signal + 1 >= bars.Length || bars[signal + 1].T != bars[signal].T.AddMinutes(1))
        {
            why = "first breakout missing next-minute entry candle";
            return null;
        }
        var enter = bars[signal+1];
        var direction = side.Value;
        var price = enter.C.O;
        var initial = logic.CalculateInitialStop(direction, price, range, CostAllowance);
        if (!((direction == TradeDirection.Long && initial.Stop < price) ||
              (direction == TradeDirection.Short && initial.Stop > price)))
        {
            why = "initial stop invalid at next-minute opening fill";
            return null;
        }

        var startHistory = MarketTime.ToUtc(day.AddDays(-7), TimeOnly.MinValue);
        var endHistory = MarketTime.ToUtc(day, new TimeOnly(9,45));
        var known = all.Where(x => x.T >= startHistory && x.T < endHistory)
            .Select(x => x.C).ToList();
        var references = direction == TradeDirection.Long
            ? logic.GetReferenceLevels(known, day)
            : logic.GetShortReferenceLevels(known, day);
        for (var i = 0; i <= signal; i++)
            if (bars[i].T >= endHistory) known.Add(bars[i].C);

        var trade = logic.CreateTrade(direction, enter.Contract, 1, price,
            initial.Stop, references.Intraday, references.PreviousDay,
            null, PointValue, false);
        trade.ConfirmedStop = initial.Stop;
        decimal mfe = 0m, favorablePrice = price;
        PriceBar? prior = null;
        for (var i = signal+1; i < bars.Length; i++)
        {
            var bar = bars[i];
            var et = MarketTime.ToEastern(bar.T).TimeOfDay;
            if (et >= SessionEnd) break;
            if (prior is not null && bar.T != prior.T.AddMinutes(1))
            {
                why = "gap in 1-minute bars while trade is open";
                return null;
            }
            prior = bar;
            var oldStop = trade.ConfirmedStop ?? initial.Stop;
            var stopHit = direction == TradeDirection.Long
                ? bar.C.L <= oldStop : bar.C.H >= oldStop;
            if (stopHit)
            {
                // Unknown order of high/low on the stop minute: favor stop first.
                var openedMove = direction == TradeDirection.Long
                    ? bar.C.O - price : price - bar.C.O;
                if (openedMove > mfe) { mfe = openedMove; favorablePrice = bar.C.O; }
                var stoppedAt = direction == TradeDirection.Long
                    ? Math.Min(bar.C.O, oldStop) : Math.Max(bar.C.O, oldStop);
                return Build(trade, bars[signal].T.AddMinutes(1), enter, initial.Stop,
                    bar.T.AddMinutes(1), stoppedAt, "stop", mfe, favorablePrice, day);
            }

            // On a bar without a stop touch, its high/low is reachable before exit.
            var extreme = direction == TradeDirection.Long ? bar.C.H : bar.C.L;
            var move = direction == TradeDirection.Long ? extreme - price : price - extreme;
            if (move > mfe) { mfe = move; favorablePrice = extreme; }

            known.Add(bar.C);
            // Orb's strategy object is reused. Quotes are unavailable: evaluate stage
            // changes at each candle close, and make new stops active next minute.
            for (var step = 0; step < 3; step++)
            {
                var stage = logic.GetNextStageAction(trade, bar.C.C, range, known);
                if (stage is null) break;
                if (stage.DesiredStop is decimal proposed)
                {
                    proposed = direction == TradeDirection.Long
                        ? Math.Floor(proposed / OrbStrategy.TickSize) * OrbStrategy.TickSize
                        : Math.Ceiling(proposed / OrbStrategy.TickSize) * OrbStrategy.TickSize;
                    // The live worker ignores any stop change that is not more
                    // protective, before checking whether the proposed stop is valid.
                    if (trade.ConfirmedStop is not decimal existing ||
                        logic.IsMoreProtective(direction, proposed, existing))
                    {
                        if (!logic.IsValidProtectiveStop(trade, proposed, bar.C.C))
                            return Build(trade, bars[signal].T.AddMinutes(1), enter, initial.Stop,
                                bar.T.AddMinutes(1), bar.C.C, "invalid-stop-close", mfe, favorablePrice, day);
                        trade.ConfirmedStop = proposed;
                    }
                }
                if (stage.Stage == 3 || stage.Stage == 4) logic.CompleteStage(trade, stage);
                else break;
            }

            if (et == new TimeSpan(15,59,0))
                return Build(trade, bars[signal].T.AddMinutes(1), enter, initial.Stop,
                    bar.T.AddMinutes(1), bar.C.C, "4pm-session-close", mfe, favorablePrice, day);
        }
        why = "missing 15:59 ET candle and no earlier stop";
        return null;
    }

    static TradeResult Build(TradeState trade, DateTimeOffset signalAt, PriceBar enter,
        decimal firstStop, DateTimeOffset exitAt, decimal exit, string reason,
        decimal mfe, decimal favorablePrice, DateOnly day)
    {
        var realized = trade.Direction == TradeDirection.Long ? exit-trade.EntryPrice : trade.EntryPrice-exit;
        return new TradeResult(day, enter.Contract, trade.Direction, signalAt,
            enter.T, trade.EntryPrice, firstStop, exitAt, exit, reason,
            mfe, favorablePrice, realized, trade.CompletedStage);
    }

    static List<PriceBar> Load(string path)
    {
        var rows = new SortedDictionary<DateTimeOffset, PriceBar>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine()?.TrimStart('\uFEFF');
        if (!string.Equals(header, "timestamp_utc,contract,open,high,low,close,volume",
            StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Unexpected CSV header: " + header);
        string? line;
        var lineNo = 1;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',');
            if (p.Length != 7) throw new FormatException("Invalid CSV row at line " + lineNo);
            var timestamp = DateTimeOffset.Parse(p[0], CI, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            decimal D(int column) => decimal.Parse(p[column], NumberStyles.Number, CI);
            var candle = new Candle { T = timestamp, O = D(2), H = D(3), L = D(4), C = D(5),
                V = long.Parse(p[6], CI) };
            if (candle.H < candle.L || candle.H < candle.O || candle.H < candle.C ||
                candle.L > candle.O || candle.L > candle.C)
                throw new FormatException("Invalid OHLC at line " + lineNo);
            if (!rows.TryAdd(timestamp, new PriceBar(timestamp, p[1], candle)))
                throw new FormatException("Duplicate minute at line " + lineNo);
        }
        return rows.Values.ToList();
    }

    static string FindRoot()
    {
        DirectoryInfo? folder = new(AppContext.BaseDirectory);
        while (folder != null)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Backtest for new orb.csproj")))
                return folder.FullName;
            folder = folder.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
    static string F(decimal value) => value.ToString("0.00", CI);
    static string FormatEt(DateTimeOffset time) =>
        MarketTime.ToEastern(time).ToString("yyyy-MM-ddTHH:mm:sszzz", CI);
}
