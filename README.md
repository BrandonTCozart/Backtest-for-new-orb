# ORB Configuration Optimizer — Visual Studio

Open **`Backtest for new orb.slnx`** and click the green **Play/Start** button. This performs a **recursive, exhaustive search of a deliberately bounded parameter grid** against your existing local ES 1-minute CSV.

It is **completely offline**: no Topstep API calls, no downloading, no user secrets needed, and **no live orders**. The live `Orb` repo is unchanged.

## Required input

Place **`data/es_1m_3months.csv`** in the project folder. If only **`data/es_1m_3months_INCOMPLETE.csv`** is available, that is accepted with a warning. The normal-named CSV is preferred when both exist.

Expected header: `timestamp_utc,contract,open,high,low,close,volume`.

## What gets optimized

The entry is still based on the original **9:30–9:45 AM ET opening range**, and the first trade is simulated with the original ORB stages, including FVG/reference-level stop logic.

The program recursively evaluates **all 15,552 combinations** of the following choices:

| Parameter | Tested values |
| --- | --- |
| Last eligible breakout signal close (ET) | 10:00, 10:30, 11:00 |
| Opening range width, ES points | unrestricted, 6–25, 10–40 |
| VWAP filter | off; direction confirmation; direction + 5-minute VWAP slope |
| Extra breakout close distance past OR boundary | 0, 0.5, 1 ES point |
| Breakout volume relative to prior 10 completed minutes | off, 1.25× |
| Entry on retest of OR boundary after breakout | off; on (within five 1-minute candles) |
| First trade size | **1 or 2 ES** |
| Total initial-risk cap / fallback stop risk | $600/$500 or $500/$400 |
| Original Stage 5 ES trailing distance | $150, $250, $350 |
| Optional Stage 6 size after a losing stopped-out ES trade | **0, 5, 10 or 20 MES** |

These combinations are explicitly **bounded**, not an attempt to search literally every real-valued stop setting. The original Stage 3/4 thresholds, Stage 5 activation threshold, and Stage 6 MES stop stages stay as in the `Orb` strategy. **This optimizer does not use or compare the $500/$450 profit-lock system** that was used by the preceding experiment.

When retest is enabled, the simulator first detects a qualifying breakout, then waits up to five bars for the opening-range boundary to be touched and for price to close outside again.

### Important MES limitation

**The CSV contains ES candles only.** A Stage 6 MES trade is simulated using the **ES price as a proxy** and the MES $5/point value, with a quantity up to **20 MES**. MES has the same tick size as ES but not necessarily the exact same prices, volume, spreads, or fills. Consequently the MES backtest is a *hypothetical proxy estimate*, **not** a verified MES historical replay. Any configuration whose P&L relies on Stage 6 needs genuine historical MES candles before considering live use.

All trades include an estimated $10-per-contract cost from the existing `Orb` risk allowance. This may differ significantly from actual commissions and slippage, especially for MES. Risk caps are total position risk, not per-contract risk.

## Ranking and protection against overfitting

The optimizer divides eligible days **chronologically**, before calculating returns:

- First 70% of complete-ORB days: **training**. Every combination is tested, and the most profitable training configuration is selected by **highest simulated net P&L**, subject to a minimum number of first ES trades. Drawdown and trade count are tie breakers.
- Last 30%: untouched **holdout**. Only the top ten training configurations are evaluated on these days. **Holdout P&L is not used to pick the winner.**

The program displays the **best training configuration**, its exact settings, total training net P&L, drawdown, trade count, and separate holdout performance. It also reports combined-period P&L strictly as descriptive context.

**Thousands of strategies tested on only a few weeks of ES history create significant overfitting risk.** Even a good holdout result on such a short period is weak evidence of future profitability. Increase sample coverage and retest before using a candidate with real money.

## CSV output

All reports go in your local `data` folder:

- **`optimizer_all_configurations.csv`** — all 15,552 tested combinations with settings, training P&L, drawdown, number of trades, and eligibility.
- **`optimizer_top10_holdout.csv`** — the top ten ranked on training results, with corresponding holdout results.
- **`optimizer_best_trades.csv`** — first ES and any simulated MES trades for the best configuration, including per-trade and running net P&L; labels MES rows `MES_ES_PRICE_PROXY`.
- **`optimizer_best_monthly_pnl.csv`** — monthly gross P&L, estimated costs, net P&L, cumulative net P&L.
- **`optimizer_best_configuration.txt`** — human-readable winning parameters and training/holdout breakdown.

Missing bars during an active trade cause it to be excluded. Missing entire dates in an incomplete source cannot be reconstructed.

## One-minute backtest limitations

Entry is assumed at the **next candle's open** after a qualifying signal. Original stop stages react to **candle closes** rather than to individual quote updates as the live worker does. An already-active stop is assumed to trigger first if the same minute also has a favorable wick; gap-through-stop exits use the candle opening price. These are simulation choices, not broker-confirmed fills. Trading costs are fixed estimates.

The recursive grid search may run for an extended period on slower computers. Progress is printed every 250 configurations. Close the running program or use Visual Studio Stop to cancel. The program writes final reports when all configurations have been evaluated.

**Development status:** These changes were committed to GitHub but have not been compiled or run with your private CSV in this environment.
