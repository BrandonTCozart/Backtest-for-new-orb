# ORB Walk-Forward Optimizer

Open **`Backtest for new orb.slnx`** in Visual Studio, pull the latest changes and press **Play**. The default runner is now **expanding-window walk-forward testing**, replacing the old one-time 70/30 holdout. The application uses your existing local ES candles, **without contacting Topstep or placing live orders**. The live `Orb` repository remains unchanged.

## Input

Keep either `data/es_1m_3months.csv` (preferred) or `data/es_1m_3months_INCOMPLETE.csv` in the project folder.

The file must contain `timestamp_utc,contract,open,high,low,close,volume`. The program requires **at least 20 trading sessions with a complete 9:30–9:45 AM ET opening range**. Missing dates in an incomplete download cannot be inferred.

## How the walk-forward test works

Sessions are split **chronologically into five consecutive blocks** (the final block can include extra days). The optimizer runs **three expanding-window folds**:

| Fold | Training data (used to pick configuration) | Following test data (never used to choose it) |
| --- | --- | --- |
| 1 | First 40% (blocks 1–2) | Next ~20% (block 3) |
| 2 | First 60% (blocks 1–3) | Next ~20% (block 4) |
| 3 | First 80% (blocks 1–4) | Final ~20% (block 5) |

For **each fold**, all **642,816 configurations** are evaluated on its **training** data only. The most profitable *qualifying* training configuration is selected by **net P&L**, with lower closed-trade drawdown and higher first-trade count as tie-breakers. A configuration must have at least `max(4, ceiling(training sessions / 4))` completed ES first trades to qualify.

**Only after training selection** is that fold's winner simulated on the immediately following **unseen test block**. The winning settings can differ between folds. Test blocks never overlap, and their P&L is **not used to pick or revise the configurations**.

The main result is **combined walk-forward out-of-sample net P&L**, formed by stitching the three test blocks together. A candidate winning on training but losing on later test blocks is explicitly visible; do not interpret the latest settings as proven profitable.

The original parameter grid remains unchanged: entry cutoff (10:00, 10:30, 11:00, or 16:00 ET), all 31 nonempty weekday combinations, opening-range width filters, VWAP confirmation, breakout price buffer, relative volume, retest option, two initial-risk configurations, ES Stage-5 trails of $150/$250/$350, first position of **1 or 2 ES**, and optional Stage-6 reversal of **0/5/10/20 MES**.

This runs the original Orb stage-based stop rules, **not** the previously tested $500/$450 profit-lock ladder.

## Results

The console displays the exact training/test date ranges, the selected configuration and training net P&L for each fold, next-block test net P&L, and the **total stitched out-of-sample net P&L**, costs, trade count, drawdown, and number of profitable test blocks. The **most recent fold's winner** is printed as a potential *candidate*, not a guaranteed strategy for future trading.

New CSV/text reports are written to `data`:

- **`optimizer_walk_forward_folds.csv`** — each fold's selected settings, training results, immediately following test results, dates and trade counts.
- **`optimizer_walk_forward_trades.csv`** — out-of-sample trades only, with fold ID, net P&L, running out-of-sample equity and labeled MES proxies.
- **`optimizer_walk_forward_monthly_pnl.csv`** — monthly out-of-sample gross/net P&L and running balance.
- **`optimizer_walk_forward_all_configurations.csv`** — each of the 642,816 configurations with its **training-only** metrics for all three folds. The file is streamed while running and can be large; rows are not sorted by P&L.
- **`optimizer_walk_forward_summary.txt`** — fold-by-fold results, combined performance and latest selected settings.
- **`optimizer_trade_pattern_observations.csv`** — one row per walk-forward **ES first trade**, listing date, winner/loser label and pre-entry opening range, breakout strength, signal-candle shape, relative volume, ATR, prior candle-range ratio, VWAP alignment, five-minute direction, and entry timing; exit reason and MFE are outcome-only descriptive fields.
- **`optimizer_trade_pattern_comparisons.csv`** — for each predefined condition, the number of **winning and losing** trades both **with and without** the condition, their respective loss rates, average P&L, percentage-point difference, missing-data count, and a minimum-sample flag.
- **`optimizer_trade_pattern_summary.txt`** — readable higher- and lower-loss-rate associations, with explicit warnings about small samples and overfitting.

### Trade-pattern analysis (automatically follows the optimizer)

Once the three walk-forward test blocks finish, the program examines **only the out-of-sample ES first trades** to describe *which conditions occurred more often in losing trades than winning trades*. It checks weekdays, entry timing, trade direction, OR width, breakout distance, signal-bar body and close location, relative signal volume and candle range, 14-minute ATR, VWAP alignment, and the preceding five minutes' price direction. It also reports the **opposite** group, so it never assumes a condition is loss-specific: a condition might appear in both winners and losers, or in too few trades to be useful.

Pre-entry metrics read **only the completed breakout signal candle and earlier 1-minute candles** (the entry bar's future high/low/close is never inspected); missing contiguous-bar history yields a blank measurement rather than a fabricated value. MES reversal price-proxy trades are excluded from the ES first-trade statistics and explicitly counted as excluded.

The console highlights up to four criteria with higher observed loss rates and four with lower rates **only when each comparison side has at least five trades**. The full comparison CSV includes all conditions, including zero/low-support ones. This support threshold is **not** a significance test. The diagnostic doesn't change the strategy, optimize a new filter, or modify the walk-forward P&L.

**Important:** Looking for patterns in the out-of-sample test labels makes those dates useful for *exploration*, but **not untouched for later testing of newly developed rules**. Test any changes motivated by these patterns on *fresh, later historical data* instead of claiming the old walk-forward periods independently confirm the new rules.

Older `optimizer_best_...`, `optimizer_top10_holdout.csv`, and `optimizer_all_configurations.csv` files from previous runs may still exist locally, but the new walk-forward runner **does not update them**.

## Accuracy and limitations

- ES one-minute OHLC candles do not show quote-by-quote price movement. Market entries are estimated at the next minute's open; stage-based trailing stops update at candle close, and existing stops are assumed touched before favorable wicks in ambiguous candles. Real fills and trading costs may differ.
- Optional MES Stage-6 trades use ES candles as a **price proxy**, with the MES $5-per-point multiplier; **actual MES prices were not downloaded**. Treat MES P&L as an approximation.
- Estimated round-trip cost is **$10 per contract**, as in the prior simulation. The reported drawdown is calculated on **closed-trade equity**, not intratrade swings.
- **Walk-forward helps expose overfitting, but does not eliminate it**. A short dataset and a 642,816-configuration search can still pick lucky settings. Three test blocks over a short period are not proof of future profitability.
- Each base configuration's daily trade outcomes are computed once and reused for the 31 weekday filters and all three folds. Progress is printed every 100 base configurations. The optimizer may take a long time; stopping early leaves only a partial all-configurations CSV.

**Development status:** The trade-pattern diagnostic changes are committed to GitHub, but this environment has not compiled or executed them against your local ES CSV.
