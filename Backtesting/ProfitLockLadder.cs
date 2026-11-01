namespace Backtest_for_new_orb;

// All dollar amounts are gross unrealized P&L for one ES contract ($50/point).
// This is a stop ladder, NOT a $500 take-profit/limit order.
public static class ProfitLockLadder
{
    public const decimal EsPointValue = 50m;
    public const decimal ActivationProfit = 500m;
    public const decimal FirstLockedProfit = 450m;
    public const decimal StepProfit = 200m;
    public const decimal TickSize = 0.25m;

    public static decimal? LockedProfitFromFavorablePoints(decimal favorablePoints)
    {
        var favorableDollars = favorablePoints * EsPointValue;
        if (favorableDollars < ActivationProfit)
            return null;

        var steps = Math.Floor((favorableDollars - ActivationProfit) / StepProfit);
        return FirstLockedProfit + steps * StepProfit;
    }

    public static decimal StopPriceFromLockedProfit(
        decimal entry, bool isLong, decimal lockedProfit)
    {
        var price = isLong
            ? entry + lockedProfit / EsPointValue
            : entry - lockedProfit / EsPointValue;

        return isLong
            ? Math.Floor(price / TickSize) * TickSize
            : Math.Ceiling(price / TickSize) * TickSize;
    }
}
