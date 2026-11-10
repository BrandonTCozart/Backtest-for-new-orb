using System.Globalization;
using Orb;

namespace Backtest_for_new_orb;

// Entirely read-only research. A "day" is the US regular-session date;
// its overnight trading window begins at 18:00 ET on the preceding calendar day.
public enum MultiStrategyKind
{
    Orb,
    MorningMomentum,
    MorningVwapReversion,
    AfternoonMomentum,
    AfternoonVwapReversion,
    OvernightMomentum,
    OvernightReversion,
    MorningSupportResistanceBounce,
    AfternoonSupportResistanceBounce,
    MorningSupportResistanceBreakout,
    AfternoonSupportResistanceBreakout,
    MorningBreakoutRetest,
    AfternoonBreakoutRetest,
    MorningFibonacciRetracement,
    AfternoonFibonacciRetracement,
    MorningFibonacciExtension,
    AfternoonFibonacciExtension,
    MorningLiquiditySweep,
    AfternoonLiquiditySweep
}

public enum MultiStrategySlot { Overnight, Morning, Afternoon }

// Reference-price families are only calculated from fully completed candles
// and historical sessions belonging to the same ES contract.
public enum PriceLevelSource { OpeningRange, PreviousCompleteRth, Overnight, Rolling }

// Deep is the regular SQL research grid. Smoke is kept small for CI regression
// tests and short iterations; it must never silently be reported as Deep.
public enum MultiSearchProfile { Smoke, Deep }

public sealed record MultiStrategyVariant(
    int Id, MultiStrategyKind Kind, int Lookback, decimal Threshold,
    decimal StopAtrMultiple, decimal RewardRisk, decimal VolumeMultiple,
    int OrbCutoffMinutes, decimal OrbTrailDollars,
    EconomicEventRule EventRule,
    int WeekdayMask = WeekdaySelection.All,
    int StartShiftMinutes = 0,
    decimal RsiExtreme = 65m,
    bool TrendConfirmation = true,
    VwapRule OrbVwap = VwapRule.Off,
    decimal OrbRangeMin = 0m,
    decimal OrbRangeMax = 10000m,
    bool OrbRetest = false,
    decimal OrbRiskCap = 600m,
    decimal OrbFallbackRisk = 500m,
    MultiStrategySlot? ResearchSlot = null,
    PriceLevelSource LevelSource = PriceLevelSource.OpeningRange,
    decimal FibRatio = 0.618m,
    int RetestBars = 8)
{
    public MultiStrategySlot Slot => ResearchSlot ?? (Kind switch
    {
        MultiStrategyKind.OvernightMomentum or MultiStrategyKind.OvernightReversion => MultiStrategySlot.Overnight,
        MultiStrategyKind.AfternoonMomentum or MultiStrategyKind.AfternoonVwapReversion or
        MultiStrategyKind.AfternoonSupportResistanceBounce or
        MultiStrategyKind.AfternoonSupportResistanceBreakout or
        MultiStrategyKind.AfternoonBreakoutRetest or
        MultiStrategyKind.AfternoonFibonacciRetracement or
        MultiStrategyKind.AfternoonFibonacciExtension or
        MultiStrategyKind.AfternoonLiquiditySweep => MultiStrategySlot.Afternoon,
        _ => MultiStrategySlot.Morning
    });

    // This is a single CSV field: semicolons separate disclosed settings.
    public string Parameters => string.Join("; ",
        $"kind={Kind}", $"lookback={Lookback}", $"signal_threshold={Threshold}",
        $"stop_ATR_multiple={StopAtrMultiple}", $"reward_risk={RewardRisk}",
        $"relative_volume={VolumeMultiple}", $"ORB_cutoff_minutes={OrbCutoffMinutes}",
        $"ORB_stage5_trail_dollars={OrbTrailDollars}",
        $"economic_event_rule={EventRule}",
        $"weekdays={WeekdaySelection.Format(WeekdayMask)}",
        $"start_shift_minutes={StartShiftMinutes}",
        $"RSI_extreme={RsiExtreme}", $"trend_confirmation={TrendConfirmation}",
        $"ORB_vwap={OrbVwap}", $"ORB_range={OrbRangeMin}-{OrbRangeMax}",
        $"ORB_retest={OrbRetest}", $"ORB_initial_risk_cap={OrbRiskCap}",
        $"ORB_fallback_risk={OrbFallbackRisk}",
        $"research_slot={Slot}", $"reference_source={LevelSource}",
        $"fib_ratio={FibRatio}", $"retest_window_bars={RetestBars}");
}

public sealed class MultiIndicators
{
    public required decimal[] Atr14 { get; init; }
    public required decimal[] Ema9 { get; init; }
    public required decimal[] Ema21 { get; init; }
    public required decimal[] Vwap { get; init; }

    public static MultiIndicators Calculate(PriceBar[] bars)
    {
        var n = bars.Length;
        var atr = new decimal[n];
        var fast = new decimal[n];
        var slow = new decimal[n];
        var vwap = new decimal[n];
        decimal trueRangeSum = 0, pv = 0, vol = 0;
        for (int i = 0; i < n; i++)
        {
            var c = bars[i].C;
            var prior = i == 0 ? c.C : bars[i - 1].C.C;
            var tr = Math.Max(c.H - c.L,
                Math.Max(Math.Abs(c.H - prior), Math.Abs(c.L - prior)));
            // ATR14 is unknown until 14 completed candles exist.
            trueRangeSum += tr;
            if (i >= 14)
            {
                var old = bars[i - 14].C;
                var oldPrior = i == 14 ? old.C : bars[i - 15].C.C;
                trueRangeSum -= Math.Max(old.H - old.L,
                    Math.Max(Math.Abs(old.H - oldPrior), Math.Abs(old.L - oldPrior)));
            }
            atr[i] = i >= 13 ? trueRangeSum / 14m : 0m;
            fast[i] = i == 0 ? c.C : (2m / 10m) * c.C + (8m / 10m) * fast[i - 1];
            slow[i] = i == 0 ? c.C : (2m / 22m) * c.C + (20m / 22m) * slow[i - 1];
            var size = Math.Max(0L, c.V);
            pv += ((c.H + c.L + c.C) / 3m) * size;
            vol += size;
            vwap[i] = vol > 0 ? pv / vol : c.C;
        }
        return new MultiIndicators { Atr14 = atr, Ema9 = fast, Ema21 = slow, Vwap = vwap };
    }
}

public sealed class MultiSession
{
    public required DateOnly Day { get; init; }
    public required string Contract { get; init; }
    public required PriceBar[] Rth { get; init; }
    public required PriceBar[]? Overnight { get; init; }
    public required MultiIndicators RthIndicators { get; init; }
    public required MultiIndicators? OvernightIndicators { get; init; }

    // All values below are available by their particular session's decision time.
    public decimal? PriorCompleteRthHigh { get; init; }
    public decimal? PriorCompleteRthLow { get; init; }
    public DateOnly? PriorCompleteRthDate { get; init; }
    public required decimal OpeningHigh { get; init; }
    public required decimal OpeningLow { get; init; }
    public decimal? OvernightHigh { get; init; }
    public decimal? OvernightLow { get; init; }
    public required decimal OpeningWidth { get; init; } // 9:45 ET
    public required decimal EarlyAtr { get; init; } // 9:45 ET
    public required decimal EarlyVolumeRatio { get; init; } // 9:45 ET
    public required decimal OpeningMove { get; init; } // 9:45 ET
    public required decimal AfternoonMove { get; init; } // 13:00 ET
    public required decimal AfternoonAtr { get; init; } // 13:00 ET
    public required decimal FirstHourOvernightMove { get; init; } // 19:00 ET
    public required decimal FirstHourOvernightAtr { get; init; } // 19:00 ET
}

public sealed record MultiStrategyTrade(
    DateOnly Day, MultiStrategyKind Kind, int VariantId, string Contract,
    TradeDirection Side, DateTimeOffset EntryAt, DateTimeOffset ExitAt,
    decimal Entry, decimal Exit, decimal InitialRiskDollars,
    string ExitReason, int Quantity = 1)
{
    public decimal Gross => (Side == TradeDirection.Long ? Exit - Entry : Entry - Exit)
        * 50m * Quantity;
    public decimal Costs => 10m * Quantity; // Same round-trip assumption as the existing ES optimizer.
    public decimal Net => Gross - Costs;
    public bool IsWin => Net > 0;
}

public static class MultiStrategyData
{
    public const int RthMinuteCount = 390; // 09:30 through 15:59 ET
    public const int OvernightMinuteCount = 930; // prior day 18:00 through 09:29 ET

    public static List<MultiSession> Build(
        IReadOnlyList<PriceBar> activeBars,
        IReadOnlyList<PriceBar> allContractBars, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        // Preserve real contract identity: never bridge a futures rollover
        // with price bars belonging to another ES contract.
        var contracts = new Dictionary<string, Dictionary<DateTimeOffset, PriceBar>>(
            StringComparer.OrdinalIgnoreCase);
        var duplicateTimes = new HashSet<(string Contract, DateTimeOffset Time)>();
        foreach (var bar in allContractBars)
        {
            if (!contracts.TryGetValue(bar.Contract, out var byTime))
            {
                byTime = new Dictionary<DateTimeOffset, PriceBar>();
                contracts.Add(bar.Contract, byTime);
            }
            if (!byTime.TryAdd(bar.T, bar))
                duplicateTimes.Add((bar.Contract.ToUpperInvariant(), bar.T));
        }

        var rows = new List<string> {
            "trading_date_et,es_contract,rth_minutes,rth_valid,overnight_minutes,overnight_valid,reason"
        };
        var valid = new List<MultiSession>();
        var excluded = 0;
        var missingOvernight = 0;
        foreach (var group in activeBars
                     .Where(b =>
                     {
                         var t = MarketTime.ToEastern(b.T);
                         return t.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday &&
                                t.TimeOfDay >= new TimeSpan(9, 30, 0) &&
                                t.TimeOfDay < new TimeSpan(16, 0, 0);
                     })
                     .GroupBy(b => DateOnly.FromDateTime(MarketTime.ToEastern(b.T).Date))
                     .OrderBy(g => g.Key))
        {
            var day = group.Key;
            var rth = group.OrderBy(b => b.T).ToArray();
            var contract = rth[0].Contract;
            var rthStart = MarketTime.ToUtc(day, new TimeOnly(9, 30));
            var rthOk = rth.Length == RthMinuteCount &&
                        rth[0].T == rthStart &&
                        rth.All(b => string.Equals(b.Contract, contract, StringComparison.OrdinalIgnoreCase)) &&
                        IsContiguous(rth);
            if (!rthOk)
            {
                excluded++;
                rows.Add($"{day:yyyy-MM-dd},{contract},{rth.Length},no,0,no,incomplete_or_mixed_RTH");
                continue;
            }

            var prior = valid.Count > 0 ? valid[^1] : null;
            // Prior complete US trading session only, same contract, within
            // four calendar days (weekends/most exchange holidays included).
            var hasPrior = prior is not null &&
                string.Equals(prior.Contract, contract, StringComparison.OrdinalIgnoreCase) &&
                day.DayNumber - prior.Day.DayNumber is >= 1 and <= 4;
            var nightStart = MarketTime.ToUtc(day.AddDays(-1), new TimeOnly(18, 0));
            var nightEnd = rthStart;
            var night = new List<PriceBar>(OvernightMinuteCount);
            var observedOvernightMinutes = 0;
            DateTimeOffset? firstOvernightGap = null;
            contracts.TryGetValue(contract, out var source);
            for (var time = nightStart; time < nightEnd; time = time.AddMinutes(1))
            {
                var validBar = source is not null &&
                    source.TryGetValue(time, out var _unused) &&
                    !duplicateTimes.Contains((contract.ToUpperInvariant(), time));
                if (!validBar)
                {
                    firstOvernightGap ??= time;
                    continue;
                }
                observedOvernightMinutes++;
                if (firstOvernightGap is null)
                    night.Add(source![time]);
            }
            var overnightOk = nightEnd - nightStart == TimeSpan.FromMinutes(OvernightMinuteCount) &&
                              firstOvernightGap is null &&
                              night.Count == OvernightMinuteCount;
            if (!overnightOk) missingOvernight++;
            var rthMetrics = MultiIndicators.Calculate(rth);
            var nightBars = overnightOk ? night.ToArray() : null;
            var nightMetrics = nightBars is null ? null : MultiIndicators.Calculate(nightBars);
            var earlyVolume = rth.Take(5).Average(x => (decimal)Math.Max(0L, x.C.V));
            var laterVolume = rth.Skip(10).Take(5).Average(x => (decimal)Math.Max(0L, x.C.V));
            var earlyHigh = rth.Take(15).Max(x => x.C.H);
            var earlyLow = rth.Take(15).Min(x => x.C.L);
            valid.Add(new MultiSession {
                Day = day,
                Contract = contract,
                PriorCompleteRthHigh = hasPrior ? prior!.Rth.Max(x => x.C.H) : null,
                PriorCompleteRthLow = hasPrior ? prior!.Rth.Min(x => x.C.L) : null,
                PriorCompleteRthDate = hasPrior ? prior!.Day : null,
                Rth = rth,
                Overnight = nightBars,
                RthIndicators = rthMetrics,
                OvernightIndicators = nightMetrics,
                OpeningHigh = earlyHigh,
                OpeningLow = earlyLow,
                OvernightHigh = nightBars is null ? null :
                    nightBars.Max(x => x.C.H),
                OvernightLow = nightBars is null ? null :
                    nightBars.Min(x => x.C.L),
                OpeningWidth = earlyHigh - earlyLow,
                EarlyAtr = rthMetrics.Atr14[14],
                EarlyVolumeRatio = earlyVolume > 0m ? laterVolume / earlyVolume : 0m,
                OpeningMove = rth[14].C.C - rth[0].C.O,
                AfternoonMove = rth[209].C.C - rth[0].C.O,
                AfternoonAtr = rthMetrics.Atr14[209],
                FirstHourOvernightMove = nightBars is null ? 0m :
                    nightBars[59].C.C - nightBars[0].C.O,
                FirstHourOvernightAtr = nightMetrics is null ? 0m : nightMetrics.Atr14[59]
            });
            var nightReason = overnightOk ? "ok" :
                "overnight_first_missing_" +
                (firstOvernightGap?.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)
                    ?? "invalid_duration");
            rows.Add($"{day:yyyy-MM-dd},{contract},{rth.Length},yes,{observedOvernightMinutes}," +
                     $"{(overnightOk ? "yes" : "no")},{nightReason}");
        }

        File.WriteAllLines(Path.Combine(reportDirectory, "multi_strategy_data_quality.csv"), rows);
        Console.WriteLine($"Multi-strategy data audit: {valid.Count:N0} complete 390-minute RTH sessions; " +
                          $"{excluded:N0} incomplete RTH sessions excluded; " +
                          $"{valid.Count - missingOvernight:N0} complete 930-minute overnight sessions.");
        Console.WriteLine("Audit: data/multi_strategy_data_quality.csv (missing overnight excludes only overnight trades).");
        return valid;
    }

    private static bool IsContiguous(PriceBar[] bars)
    {
        for (var i = 1; i < bars.Length; i++)
            if (bars[i].T != bars[i - 1].T.AddMinutes(1))
                return false;
        return true;
    }
}
