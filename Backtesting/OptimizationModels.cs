using Orb;

namespace Backtest_for_new_orb;

// The optimizer searches a bounded, disclosed grid; it never submits live orders.
public enum VwapRule { Off, Direction, DirectionAndSlope }
public sealed record RangeBand(decimal Min, decimal Max)
{
    public override string ToString() => Max >= 1000m ? "Any" : $"{Min}-{Max}";
}
public sealed record RiskBand(decimal MaxRisk, decimal FallbackRisk)
{
    public override string ToString() => $"{MaxRisk}/{FallbackRisk}";
}
public sealed record OptimizationConfig(
    int Id, TimeSpan Cutoff, RangeBand Range, VwapRule Vwap,
    decimal BreakoutPoints, decimal VolumeMultiplier, bool Retest,
    int EsQuantity, RiskBand Risk, decimal EsTrailDollars, int MesQuantity,
    int WeekdayMask = WeekdaySelection.All,
    EconomicEventRule EventRule = EconomicEventRule.Off)
{
    public string Parameters => string.Join(",",
        Cutoff.ToString(@"hh\:mm"), Range.Min, Range.Max,
        Vwap, BreakoutPoints, VolumeMultiplier, Retest,
        EsQuantity, Risk.MaxRisk, Risk.FallbackRisk, EsTrailDollars, MesQuantity,
        WeekdaySelection.Format(WeekdayMask), EventRule);
}

public sealed class SessionData
{
    public required DateOnly Day { get; init; }
    public required PriceBar[] Bars { get; init; }
    public required Candle[] PriorHistory { get; init; }
    public required OpeningRange? Range { get; init; }
    public required (decimal? Intraday, decimal? PreviousDay) LongReferences { get; init; }
    public required (decimal? Intraday, decimal? PreviousDay) ShortReferences { get; init; }
    public required decimal?[] Vwaps { get; init; }
    public required decimal?[] VolumeAverages { get; init; }
}

public sealed record SimulatedTrade(
    DateOnly Date, string Contract, bool IsMes, int Quantity, TradeDirection Direction,
    DateTimeOffset EntryAt, decimal Entry, DateTimeOffset ExitAt, decimal Exit,
    string ExitReason, decimal MfePoints, int ExitIndex)
{
    public decimal PointValue => IsMes ? 5m : 50m;
    public decimal Gross => (Direction == TradeDirection.Long ? Exit - Entry : Entry - Exit)
        * PointValue * Quantity;
    // Original Orb's config has $10 "commission and slippage" per contract.
    public decimal EstimatedCosts => 10m * Quantity;
    public decimal Net => Gross - EstimatedCosts;
    public bool Stopped => ExitReason == "stop";
}

public sealed class OptimizationResult
{
    public required OptimizationConfig Config { get; init; }
    public decimal Net { get; set; }
    public decimal Gross { get; set; }
    public decimal Costs { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int EsTrades { get; set; }
    public int MesTrades { get; set; }
    public int WinningTrades { get; set; }
    public int SkippedDays { get; set; }
    public List<SimulatedTrade>? DetailedTrades { get; set; }
    public int TotalTrades => EsTrades + MesTrades;
}

public static class OptimizationData
{
    public static List<SessionData> Prepare(List<PriceBar> all, List<PriceBar>? completeContractHistory = null)
    {
        var logic = new OrbStrategy();
        var historicalBars = completeContractHistory ?? all;
        var sessions = all.Where(x =>
        {
            var et = MarketTime.ToEastern(x.T);
            return et.TimeOfDay >= new TimeSpan(9,30,0) &&
                   et.TimeOfDay < new TimeSpan(16,0,0) &&
                   et.DayOfWeek != DayOfWeek.Saturday && et.DayOfWeek != DayOfWeek.Sunday;
        }).GroupBy(x => DateOnly.FromDateTime(MarketTime.ToEastern(x.T).Date))
          .OrderBy(x => x.Key).ToArray();

        var prepared = new List<SessionData>();
        foreach (var g in sessions)
        {
            var bars = g.OrderBy(x => x.T).ToArray();
            if (bars.Length == 0) continue;
            var day = g.Key;
            var from = MarketTime.ToUtc(day.AddDays(-7), TimeOnly.MinValue);
            var to = MarketTime.ToUtc(day, new TimeOnly(9,45));
            // The live worker requests history for the active contract.
            // Do not join price/FVG history across futures contract rolls.
            var contract = bars[0].Contract;
            var past = historicalBars.Where(x => x.T >= from && x.T < to &&
                        string.Equals(x.Contract, contract, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.C).ToArray();
            var opening = logic.GetOpeningRange(bars.Select(x => x.C), day);
            var lr = logic.GetReferenceLevels(past, day);
            var sr = logic.GetShortReferenceLevels(past, day);
            var vwaps = new decimal?[bars.Length];
            var volAverage = new decimal?[bars.Length];
            decimal pxVol = 0m, totalVolume = 0m;
            for (int i = 0; i < bars.Length; i++)
            {
                var c = bars[i].C;
                var vol = Math.Max(0, c.V);
                pxVol += (c.H + c.L + c.C) / 3m * vol;
                totalVolume += vol;
                if (totalVolume > 0) vwaps[i] = pxVol / totalVolume;

                if (i >= 10)
                {
                    decimal lastTen = 0m;
                    for (var j = i - 10; j < i; j++) lastTen += Math.Max(0, bars[j].C.V);
                    volAverage[i] = lastTen / 10m;
                }
            }
            prepared.Add(new SessionData {
                Day = day, Bars = bars, PriorHistory = past, Range = opening,
                LongReferences = lr, ShortReferences = sr, Vwaps = vwaps,
                VolumeAverages = volAverage
            });
        }
        return prepared;
    }
}
