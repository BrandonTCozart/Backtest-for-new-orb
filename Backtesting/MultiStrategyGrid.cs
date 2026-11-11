namespace Backtest_for_new_orb;

/// <summary>
/// Disclosed, deterministic exhaustive grid for ES one-minute research.
/// Factors are bounded by data resolution and available session count.
/// This grid is NOT expected to be statistically robust just because it is large.
/// The runner keeps training-only selection and chronological OOS separate.
/// </summary>
public static class MultiStrategyGrid
{
    public static IReadOnlyList<MultiStrategyVariant> Deep()
    {
        var variants = new List<MultiStrategyVariant>(50000);
        var id = 0;

        // 20,736 original staged ORB variants: diverse entry/cutoff, filters,
        // stops, weekday/event policies; one ES and no MES price proxies.
        foreach (var cutoff in new[] { 30, 60, 90 })
        foreach (var buffer in new[] { 0m, 0.25m, 0.5m, 1m })
        foreach (var volume in new[] { 0m, 1.25m, 1.5m })
        foreach (var vwap in new[] { VwapRule.Off, VwapRule.Direction })
        foreach (var range in new[] { (Min: 0m, Max: 10000m), (Min: 10m, Max: 40m) })
        foreach (var retest in new[] { false, true })
        foreach (var trail in new[] { 150m, 250m, 350m })
        foreach (var risk in new[] { (Cap: 600m, Fallback: 500m),
                                    (Cap: 500m, Fallback: 400m) })
        foreach (var weekdays in new[] { WeekdaySelection.All, 15, 14 })
        foreach (var news in Enum.GetValues<EconomicEventRule>())
            variants.Add(new MultiStrategyVariant(++id, MultiStrategyKind.Orb,
                0, buffer, 0m, 0m, volume, cutoff, trail, news,
                WeekdayMask: weekdays,
                OrbVwap: vwap,
                OrbRangeMin: range.Min, OrbRangeMax: range.Max,
                OrbRetest: retest,
                OrbRiskCap: risk.Cap, OrbFallbackRisk: risk.Fallback));

        // 4/4/4/4 factors on the non-ORB entry and bracket, with separate
        // RSI and relative-volume/trend dimensions where they have meaning.
        foreach (var kind in Enum.GetValues<MultiStrategyKind>()
                     .Where(x => x != MultiStrategyKind.Orb && !PriceActionSignals.IsPriceAction(x)))
        {
            bool momentum = kind is MultiStrategyKind.MorningMomentum or
                MultiStrategyKind.AfternoonMomentum or MultiStrategyKind.OvernightMomentum;
            bool overnight = kind is MultiStrategyKind.OvernightMomentum or
                MultiStrategyKind.OvernightReversion;
            var thresholds = momentum
                ? new[] { 0m, 0.25m, 0.5m, 1m }
                : new[] { 0.5m, 0.75m, 1.25m, 2m };
            var newsRules = overnight
                ? new[] { EconomicEventRule.Off,
                          EconomicEventRule.AvoidMajorReleaseDay,
                          EconomicEventRule.AvoidFomcDayAndPriorTradingDay }
                : Enum.GetValues<EconomicEventRule>();

            // Ordered by the ENTRY factors first, so entry signal caching
            // needs only one day's signal per candidate entry recipe.
            foreach (var lookback in new[] { 5, 10, 20, 30 })
            foreach (var threshold in thresholds)
            foreach (var shift in new[] { 0, 30 })
            foreach (var signalControl in momentum ? new[] { 0, 1 } : new[] { 60, 65, 70 })
            foreach (var relativeVolume in momentum ? new[] { 0m, 1.25m } : new[] { 0m })
            foreach (var stopAtr in new[] { 0.75m, 1m, 1.5m, 2m })
            foreach (var reward in new[] { 1m, 1.5m, 2m, 3m })
            foreach (var news in newsRules)
                variants.Add(new MultiStrategyVariant(++id, kind,
                    lookback, threshold, stopAtr, reward, relativeVolume,
                    0, 0m, news,
                    StartShiftMinutes: shift,
                    RsiExtreme: momentum ? 65m : signalControl,
                    TrendConfirmation: !momentum || signalControl == 1));
        }
        // Twelve independent morning/afternoon price-action families:
        // S/R bounce, S/R breakout, retest, Fibonacci retracement,
        // Fibonacci extension and liquidity sweep. This is purposefully
        // explicit; the backtest does not infer profitable levels hindsight.
        foreach (var kind in Enum.GetValues<MultiStrategyKind>()
                     .Where(PriceActionSignals.IsPriceAction))
        {
            var isFibRetrace = kind is MultiStrategyKind.MorningFibonacciRetracement or
                MultiStrategyKind.AfternoonFibonacciRetracement;
            var isFibExtension = kind is MultiStrategyKind.MorningFibonacciExtension or
                MultiStrategyKind.AfternoonFibonacciExtension;
            var fib = isFibRetrace || isFibExtension;
            var retest = kind is MultiStrategyKind.MorningBreakoutRetest or
                MultiStrategyKind.AfternoonBreakoutRetest;
            var sources = fib
                ? new[] { PriceLevelSource.Rolling }
                : Enum.GetValues<PriceLevelSource>();
            var ratios = isFibRetrace
                ? new[] { 0.382m, 0.5m, 0.618m }
                : isFibExtension
                    ? new[] { 1.272m, 1.618m }
                    : new[] { 0.618m };
            foreach (var lookback in new[] { 20, 40, 60 })
            foreach (var threshold in new[] { 0.25m, 0.75m })
            foreach (var source in sources)
            foreach (var ratio in ratios)
            foreach (var retestWindow in retest ? new[] { 5, 12 } : new[] { 8 })
            foreach (var shift in new[] { 0, 30 })
            foreach (var stopAtr in new[] { 1m, 1.5m })
            foreach (var reward in new[] { 1m, 1.5m, 2m })
            foreach (var news in Enum.GetValues<EconomicEventRule>())
                variants.Add(new MultiStrategyVariant(++id, kind, lookback,
                    threshold, stopAtr, reward, 0m, 0, 0m, news,
                    StartShiftMinutes: shift, LevelSource: source,
                    FibRatio: ratio, RetestBars: retestWindow));
        }
        // Preserve all original fixed-target and 58,624 trailing IDs.
        AppendExitOptions(variants, deep: true);
        return variants;
    }

    // The first pass preserves old Deep and Smoke trailing variant IDs.
    internal static void AppendExitOptions(List<MultiStrategyVariant> variants, bool deep)
    {
        var originals = variants.Where(v => v.Kind != MultiStrategyKind.Orb &&
            (v.RewardRisk == 1m || (!deep && PriceActionSignals.IsPriceAction(v.Kind))))
            .ToArray();
        var id = variants[^1].Id;
        foreach (var v in originals)
        foreach (var activation in new[] { 0.5m, 1m })
        foreach (var distance in new[] { 0.5m, 1m })
            variants.Add(v with {
                Id = ++id, RewardRisk = 0m, ExitMode = MultiExitMode.TrailingStop,
                TrailActivationR = activation, TrailAtrMultiple = distance
            });

        if (deep)
        {
            // 25 activation/distance pairs total, including the preserved four.
            foreach (var v in originals)
            foreach (var activation in new[] { 0.5m, 1m, 1.5m, 2m, 3m })
            foreach (var distance in new[] { 0.5m, 1m, 1.5m, 2m, 3m })
            {
                if ((activation is 0.5m or 1m) && (distance is 0.5m or 1m))
                    continue;
                variants.Add(v with {
                    Id = ++id, RewardRisk = 0m, ExitMode = MultiExitMode.TrailingStop,
                    TrailActivationR = activation, TrailAtrMultiple = distance
                });
            }
            // Selected cost-aware break-even options, without doubling the grid.
            foreach (var v in originals)
            foreach (var activation in new[] { 1m, 2m })
            foreach (var distance in new[] { 1m, 2m })
                variants.Add(v with {
                    Id = ++id, RewardRisk = 0m, ExitMode = MultiExitMode.TrailingStop,
                    TrailActivationR = activation, TrailAtrMultiple = distance,
                    TrailBreakeven = true
                });
        }
        else
            foreach (var v in originals)
                variants.Add(v with {
                    Id = ++id, RewardRisk = 0m, ExitMode = MultiExitMode.TrailingStop,
                    TrailActivationR = 2m, TrailAtrMultiple = 1.5m,
                    TrailBreakeven = true
                });

        foreach (var v in originals)
        foreach (var lookback in deep ? new[] { 20, 60 } : new[] { 20 })
            variants.Add(v with {
                Id = ++id, RewardRisk = 0m, ExitMode = MultiExitMode.StructureTarget,
                TargetLookbackBars = lookback
            });

        // +0.5R -> entry stop, +1R -> lock +0.5R, +2R -> ATR trail.
        foreach (var v in originals)
        foreach (var distance in deep ? new[] { 0.5m, 1m } : new[] { 1m })
            variants.Add(v with {
                Id = ++id, RewardRisk = 0m, ExitMode = MultiExitMode.StagedProfitLock,
                TrailAtrMultiple = distance
            });

        // At the R:R milestone, activate a trail INSTEAD of taking profit.
        foreach (var v in originals)
        foreach (var trigger in deep ? new[] { 1.5m, 2m } : new[] { 2m })
            variants.Add(v with {
                Id = ++id, RewardRisk = trigger, ExitMode = MultiExitMode.TargetThenTrail,
                TrailActivationR = trigger, TrailAtrMultiple = 1m
            });
    }
}
