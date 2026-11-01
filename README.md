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

For **each fold**, all **2,571,264 configurations** are evaluated on its **training** data only. The most profitable *qualifying* training configuration is selected by **net P&L**, with lower closed-trade drawdown and higher first-trade count as tie-breakers. A configuration must have at least `max(4, ceiling(training sessions / 4))` completed ES first trades to qualify.

**Only after training selection** is that fold's winner simulated on the immediately following **unseen test block**. The winning settings can differ between folds. Test blocks never overlap, and their P&L is **not used to pick or revise the configurations**.

The report now shows **two separate P&L views**:

- **Overall/full-dataset P&L:** Replays each fold's selected configuration, unchanged, over *all* available complete opening-range sessions, including its training dates. Each of the three selected configurations gets an independent full-sample P&L, trade counts and closed-trade drawdown. **Do not add the three full-dataset totals** because their date ranges overlap completely.
- **Walk-forward out-of-sample P&L:** Stitches the three immediately following test blocks together, applying the configuration selected for *each* fold. The three test blocks do not overlap.

For each fold, the console prints its **same-configuration full-dataset P&L** directly under that fold's next-block test P&L. After all folds, a **FULL-DATASET RESULTS** section shows all three full-sample totals side by side with their corresponding test-block P&L and highlights the most recently selected configuration's overall result.

The original entry/exit parameter grid remains unchanged: entry cutoff (10:00, 10:30, 11:00, or 16:00 ET), all 31 nonempty weekday combinations, opening-range width filters, VWAP confirmation, breakout price buffer, relative volume, retest option, two initial-risk configurations, ES Stage-5 trails of $150/$250/$350, first position of **1 or 2 ES**, and optional Stage-6 reversal of **0/5/10/20 MES**.

### Scheduled economic releases

The walk-forward optimizer now tests an additional **four-state economic-event rule**, multiplying 642,816 base/weekday configurations by four:

| Event rule | Trade behavior |
| --- | --- |
| `Off` | Trade without economic calendar restrictions |
| `AvoidMajorReleaseDay` | Skip the day's ES entry on major scheduled economic release days |
| `AvoidIntradayReleaseWindow` | Skip an ES entry when it falls within **30 minutes before through 15 minutes after** a scheduled 10:00 AM or 2:00 PM ET release |
| `AvoidFomcDayAndPriorTradingDay` | Skip the Fed decision date and the immediately preceding Monday-Friday trading day |

All four policies are ranked using **training dates only** for each fold, with the selected rule then applied to the following test block. For a rejected first ES entry, the optimizer does not search for a later replacement entry that day. The event rule also applies to optional Stage-6 MES entries.

The bundled August–October 2026 calendar has scheduled dates and ET times for **CPI, monthly employment/NFP, PPI, PCE, GDP, FOMC decisions and minutes, ISM manufacturing/services, JOLTS, retail sales, and ECI**. It uses verified schedule dates from [BLS](https://www.bls.gov/schedule/2026/), [BEA](https://www.bea.gov/news/schedule/full), [Federal Reserve](https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm), [ISM](https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/) and [Census](https://www.census.gov/retail/release_schedule.html).

The generated `data/optimizer_economic_calendar_2026.csv` lists the precise dates, event names, release times, and source URLs. **Anticipation of Fed moves** is represented by proximity to scheduled FOMC decisions, not an invented historical rate-cut probability. This version does not import point-in-time consensus forecasts, actual economic surprises, unscheduled headlines, or earnings releases. If additional historical data extends beyond October 2026, the bundled calendar must also be extended.

This runs the original Orb stage-based stop rules, **not** the previously tested $500/$450 profit-lock ladder.

## Results

The console displays the exact training/test date ranges, the selected configuration and training net P&L for each fold, next-block test net P&L, and the **total stitched out-of-sample net P&L**, costs, trade count, drawdown, and number of profitable test blocks. The **most recent fold's winner** is also printed with its settings.

New CSV/text reports are written to `data`:

- **`optimizer_walk_forward_folds.csv`** — each fold's selected settings, training results, immediately following test results, dates, trade counts, and full-dataset comparison metrics.
- **`optimizer_full_sample_comparison.csv`** — three rows, one per selected fold winner, with the fixed configuration's **full-dataset** net P&L, fees, trades, wins, drawdown, and its corresponding next-block test P&L.
- **`optimizer_full_sample_latest_config_trades.csv`** — every trade taken by the most recent fold winner when replayed across the *entire* dataset, including cumulative net P&L and its chronological period.

- **`optimizer_economic_calendar_2026.csv`** — official historical release schedule used by the optimizer.
- **`optimizer_walk_forward_trades.csv`** — out-of-sample trades only, with fold ID, net P&L, running out-of-sample equity and labeled MES proxies.
- **`optimizer_walk_forward_monthly_pnl.csv`** — monthly out-of-sample gross/net P&L and running balance.
- **`optimizer_walk_forward_all_configurations.csv`** — each of the 2,571,264 configurations with its **training-only** metrics for all three folds. The file is streamed while running and can be large; rows are not sorted by P&L.
- **`optimizer_walk_forward_summary.txt`** — fold-by-fold results, combined performance and latest selected settings.
- **`optimizer_trade_pattern_observations.csv`** — one row per walk-forward **ES first trade**, listing date, winner/loser label and pre-entry opening range, breakout strength, signal-candle shape, relative volume, ATR, prior candle-range ratio, VWAP alignment, five-minute direction, and entry timing; exit reason and MFE are outcome-only descriptive fields.
- **`optimizer_trade_pattern_comparisons.csv`** — for each predefined condition, the number of **winning and losing** trades both **with and without** the condition, their respective loss rates, average P&L, percentage-point difference, missing-data count, and a minimum-sample flag.
- **`optimizer_trade_pattern_summary.txt`** — win/loss condition comparisons, including market catalyst context.
- **`optimizer_economic_event_performance.csv`** — wins/losses and average ES trade P&L on major-release, CPI, employment, PCE and FOMC-related days versus other days.

### Trade-pattern analysis (automatically follows the optimizer)

Once the three walk-forward test blocks finish, the program examines **only the out-of-sample ES first trades** to describe *which conditions occurred more often in losing trades than winning trades*. It checks weekdays, entry timing, trade direction, OR width, breakout distance, signal-bar body and close location, relative signal volume and candle range, 14-minute ATR, VWAP alignment, and the preceding five minutes' price direction. It also reports the **opposite** group, so it never assumes a condition is loss-specific: a condition might appear in both winners and losers, or in too few trades to be useful.

Pre-entry metrics read **only the completed breakout signal candle and earlier 1-minute candles** (the entry bar's future high/low/close is never inspected); missing contiguous-bar history yields a blank measurement rather than a fabricated value. MES reversal price-proxy trades are excluded from the ES first-trade statistics and explicitly counted as excluded.

The console highlights up to four conditions with higher observed loss rates and four with lower rates when each comparison side has at least five trades. It separately prints all economic-calendar comparisons, including small groups. The full CSV includes every predefined condition. The diagnostic does not alter the optimizer or its trade outcomes.


Older `optimizer_best_...`, `optimizer_top10_holdout.csv`, and `optimizer_all_configurations.csv` files from previous runs may still exist locally, but the new walk-forward runner **does not update them**.

## Accuracy and limitations

- ES one-minute OHLC candles do not show quote-by-quote price movement. Market entries are estimated at the next minute's open; stage-based trailing stops update at candle close, and existing stops are assumed touched before favorable wicks in ambiguous candles. Real fills and trading costs may differ.
- Optional MES Stage-6 trades use ES candles as a **price proxy**, with the MES $5-per-point multiplier; **actual MES prices were not downloaded**. Treat MES P&L as an approximation.
- Estimated round-trip cost is **$10 per contract**, as in the prior simulation. The reported drawdown is calculated on **closed-trade equity**, not intratrade swings.
- The event-calendar policies are applied without using release outcomes, and train/test date boundaries remain chronological.
- Each base configuration's daily trade outcomes are computed once and reused for the 31 weekday filters and four economic-event policies and all three folds. Progress is printed every 100 base configurations. The optimizer may take a long time; stopping early leaves only a partial all-configurations CSV.

**Development status:** Economic-calendar integration is committed to GitHub. The local ES CSV is not available in this environment, and compilation/execution have not been verified here.
