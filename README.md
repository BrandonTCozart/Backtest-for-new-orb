# ORB Full-Dataset Optimizer + Walk-Forward Test

Open **`Backtest for new orb.slnx`** in Visual Studio, pull the latest changes and press **Play**. Startup **downloads the available TopstepX ES 1-minute candle history to a new CSV first**, then runs the existing full-dataset optimization and expanding-window walk-forward tests. All the existing optimizer and reporting functionality remains. The live `Orb` trading repository remains unchanged.

## Automatically download TopstepX 1-minute ES history

**Credentials:** The backtester reads the **original backtest project's Visual Studio User Secrets store** (the same `UserSecretsId` used by its original historical downloader). It also supports the separate Orb secrets store and the temporary backtest secrets ID from later revisions. **Existing `secrets.json` credentials do not need to be re-entered or copied into source code.**

If you want separate backtest credentials instead, right-click the backtest project → **Manage User Secrets** and use:

```json
{
  "Topstep": {
    "UserName": "YOUR_TOPSTEPX_LOGIN",
    "ApiKey": "YOUR_TOPSTEPX_API_KEY"
  }
}
```

Alternatively set the Windows user environment variables `TOPSTEP_USERNAME` and `TOPSTEP_API_KEY`. User Secrets and environment variables are kept outside this repository. Do not put API keys in committed source files.

Every time you press **Start**, the downloader authenticates to the ProjectX/TopstepX API and saves/updates `data/es_1m_max_available.csv`. It uses the **read-only** `/api/Contract/available` and `/api/History/retrieveBars` endpoints; it cannot place or modify orders. It requests one-minute OHLCV history for successive quarterly ES contracts, in 12-day windows (under the API's 20,000-bars-per-request maximum), respects rate limits, merges existing candles and retains a single ES contract per Eastern trading day.

The **first successful download scans backward through historical quarters**, ending after eight consecutive quarters without retrievable minute data (or the beginning of ES contract history). Because TopstepX may not retain expired-contract one-minute history, the actual oldest date depends on the API. Subsequent starts refresh the latest candles; delete `data/es_1m_max_available.backfill-complete` to force a full historical rescan. The downloader prints dates, candle counts and which contracts supplied data.

The original `data/es_1m_3months.csv` file remains unchanged. The optimizer **prefers the new `es_1m_max_available.csv`**, falling back to the original CSV if history downloading is unavailable. If credentials are not configured, the downloader prints setup instructions and still runs the existing backtest on the local CSV.

TopstepX API references: [Retrieve historical bars](https://gateway.docs.projectx.com/docs/api-reference/market-data/retrieve-bars/) · [Rate limits](https://gateway.docs.projectx.com/docs/getting-started/rate-limits/).



## Input

The downloader produces `data/es_1m_max_available.csv` (preferred). The existing `data/es_1m_3months.csv` and `data/es_1m_3months_INCOMPLETE.csv` are still supported as fallback inputs; neither is overwritten.

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

The optimizer now separately selects **one best overall fixed configuration** from **all 2,571,264 candidates scored across 100% of the available historical sessions**. It ranks qualifying candidates by highest full-dataset net P&L, then lower closed-trade drawdown, then more ES trades, then configuration ID. Qualification requires at least `max(4, ceiling(full sessions / 4))` completed first ES trades. Every optional feature, including weekday and economic-event filters, may be off if another choice ranks better. Full-dataset selection does not change or influence the three walk-forward fold winners.

The report shows **three separate P&L views**:

- **Best overall full-dataset configuration:** The highest-ranked qualifying setting from the entire 2,571,264-configuration search, evaluated on all complete opening-range sessions. Its full-sample net P&L, costs, trade counts, drawdown, individual settings and every trade are reported.
- **Full-dataset fold-winner replays:** Separately replay the three configurations originally selected within the walk-forward training folds across all available sessions. **Do not add those totals**, because each covers the same period.
- **Walk-forward out-of-sample P&L:** Stitches the three immediately following test blocks together, applying the configuration selected for *each* fold. The three test blocks do not overlap.

For each fold, the console prints its **same-configuration full-dataset P&L** directly under that fold's next-block test P&L. The console also prints **BEST OVERALL CONFIGURATION**, with its settings and full-dataset P&L, and a **FULL-DATASET RESULTS** comparison showing the three fold winners. The best overall configuration is not automatically the most recent fold winner, and the stitched out-of-sample result does not measure the full-dataset winner's own out-of-sample performance.

The original entry/exit parameter grid remains unchanged: entry cutoff (10:00, 10:30, 11:00, or 16:00 ET), all 31 nonempty weekday combinations, opening-range width filters, VWAP confirmation, breakout price buffer, relative volume, retest option, two initial-risk configurations, ES Stage-5 trails of $150/$250/$350, first position of **1 or 2 ES**, and optional Stage-6 reversal of **0/5/10/20 MES**.

### Scheduled economic releases

The walk-forward optimizer now tests an additional **four-state economic-event rule**, multiplying 642,816 base/weekday configurations by four:

| Event rule | Trade behavior |
| --- | --- |
| `Off` | Trade without economic calendar restrictions |
| `AvoidMajorReleaseDay` | Skip the day's ES entry on major scheduled economic release days |
| `AvoidIntradayReleaseWindow` | Skip an ES entry when it falls within **30 minutes before through 15 minutes after** a scheduled 10:00 AM or 2:00 PM ET release |
| `AvoidFomcDayAndPriorTradingDay` | Skip the Fed decision date and the immediately preceding Monday-Friday trading day |

All four policies are ranked using **training dates only** for each walk-forward fold; they are also ranked separately on **all available sessions** for best-overall selection. The selected walk-forward policy is then applied to its following test block. For a rejected first ES entry, the optimizer does not search for a later replacement entry that day. The event rule also applies to optional Stage-6 MES entries.

The bundled August–October 2026 calendar has scheduled dates and ET times for **CPI, monthly employment/NFP, PPI, PCE, GDP, FOMC decisions and minutes, ISM manufacturing/services, JOLTS, retail sales, and ECI**. It uses verified schedule dates from [BLS](https://www.bls.gov/schedule/2026/), [BEA](https://www.bea.gov/news/schedule/full), [Federal Reserve](https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm), [ISM](https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/) and [Census](https://www.census.gov/retail/release_schedule.html).

The generated `data/optimizer_economic_calendar_2026.csv` lists the precise dates, event names, release times, and source URLs. **Anticipation of Fed moves** is represented by proximity to scheduled FOMC decisions, not an invented historical rate-cut probability. This version does not import point-in-time consensus forecasts, actual economic surprises, unscheduled headlines, or earnings releases. If additional historical data extends beyond October 2026, the bundled calendar must also be extended.

This runs the original Orb stage-based stop rules, **not** the previously tested $500/$450 profit-lock ladder.

## Results

The console displays the **best overall configuration based on the entire historical dataset** and its net P&L, trade counts, fees, drawdown and settings. Separately, it displays the exact training/test date ranges for each walk-forward fold, the selected training configuration, next-block test results, and the stitched **out-of-sample P&L**. The most recent fold winner is shown separately from the best overall winner.

New CSV/text reports are written to `data`:

- **`optimizer_full_dataset_best_summary.txt`** — best fixed configuration selected from the entire full-dataset grid, its net P&L, drawdown, all settings and number of qualifying candidates.
- **`optimizer_full_dataset_best_trades.csv`** — all trades and cumulative P&L for the full-dataset optimized winner.
- **`optimizer_walk_forward_folds.csv`** — each fold's selected settings, training results, immediately following test results, dates, trade counts, and full-dataset comparison metrics.
- **`optimizer_full_sample_comparison.csv`** — three rows, one per selected fold winner, with the fixed configuration's **full-dataset** net P&L, fees, trades, wins, drawdown, and its corresponding next-block test P&L.
- **`optimizer_full_sample_latest_config_trades.csv`** — every trade taken by the most recent fold winner when replayed across the *entire* dataset, including cumulative net P&L and its chronological period.

- **`optimizer_economic_calendar_2026.csv`** — official historical release schedule used by the optimizer.
- **`optimizer_walk_forward_trades.csv`** — out-of-sample trades only, with fold ID, net P&L, running out-of-sample equity and labeled MES proxies.
- **`optimizer_walk_forward_monthly_pnl.csv`** — monthly out-of-sample gross/net P&L and running balance.
- **`optimizer_walk_forward_all_configurations.csv`** — all 2,571,264 candidates with training metrics for all three folds **plus separate full-dataset profit, costs, trade counts, drawdown and qualification status**. The file is streamed while running and can be large; rows are not sorted by P&L.
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

**Development status:** Automatic historical-download startup, full-dataset optimization and walk-forward evaluation are committed to GitHub. An authenticated TopstepX account and local .NET build are required to verify the actual available history; this environment has not executed the download or build.
