namespace Orb;

// Minimal read-only models from Orb. Trading credentials and API clients are intentionally excluded.

public enum TradeDirection
{
    Long,
    Short
}

public sealed class Candle
{
    public DateTimeOffset T { get; init; }
    public decimal O { get; init; }
    public decimal H { get; init; }
    public decimal L { get; init; }
    public decimal C { get; init; }
    public long V { get; init; }
}

public sealed class ReferenceLevel
{
    public string Name { get; init; } = string.Empty;
    public decimal Price { get; init; }
}

public sealed class OpeningRange
{
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Midpoint => (High + Low) / 2m;
    public decimal Width => High - Low;
}

public sealed class InitialStopCalculation
{
    public decimal Stop { get; init; }
    public decimal EstimatedRisk { get; init; }
    public bool UsedFallback { get; init; }
}

public sealed class FairValueGap
{
    public bool Bullish { get; init; }
    public decimal Lower { get; init; }
    public decimal Upper { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public decimal Width => Upper - Lower;
}

public sealed class OrbSessionState
{
    public DateOnly TradingDate { get; init; }
    public bool TradeSubmitted { get; set; }
    public TradeDirection? PendingEntryDirection { get; set; }
    public long? PendingEntryOrderId { get; set; }
    public string? PendingEntryContractId { get; set; }
    public int? PendingEntryQuantity { get; set; }
    public bool PendingEntryIsStage6 { get; set; }
    public DateTimeOffset? LastEntryCandle { get; set; }
    public bool Stage6Eligible { get; set; }
    public bool Stage6Submitted { get; set; }
    public DateTimeOffset? Stage6EligibleAt { get; set; }
    public TradeDirection? Stage6OriginalDirection { get; set; }
    public bool Stage6RangeRevisited { get; set; }
    public DateTimeOffset? Stage6RangeRevisitedAt { get; set; }
    public TradeState? Trade { get; set; }
    public List<long> LoggedTradeIds { get; init; } = [];
}

public sealed class TradeState
{
    public TradeDirection Direction { get; init; }
    public int Quantity { get; init; }
    public string ContractId { get; init; } = string.Empty;
    public decimal EntryPrice { get; init; }
    public long? EntryOrderId { get; init; }
    public decimal PointValue { get; init; } = 50m;
    public bool IsStage6 { get; init; }
    public decimal InitialStop { get; init; }
    public long? StopOrderId { get; set; }
    public decimal? ConfirmedStop { get; set; }
    public int CompletedStage { get; set; } = 2;
    public decimal PeakProfit { get; set; }
    public decimal? Stage4PeakProfit { get; set; }
    public decimal? Stage4SecuredProfit { get; set; }
    public List<ReferenceLevel> ReferenceLevels { get; init; } = [];
    public List<decimal> UsedReferencePrices { get; init; } = [];
}

public sealed class StageAction
{
    public int Stage { get; init; }
    public string Reason { get; init; } = string.Empty;
    public decimal? DesiredStop { get; init; }
    public decimal? ReferencePrice { get; init; }
}
