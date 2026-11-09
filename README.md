# ORB Full-Dataset Optimizer + Walk-Forward Test

Open **`Backtest for new orb.slnx`** in Visual Studio and press **Play**. The program now **reads ES futures one-minute candles directly from SQL Server `dbo.MES_DATA`**, the same table used by `BrandonTCozart/orb-analysis`. It then runs the **existing full-dataset parameter search and expanding-window walk-forward tests** unchanged. It does **not** fetch Topstep history or read candle CSV files at startup, and it never places live orders. The original download/legacy CSV utilities remain in source but are not called by Play.

## Configure SQL Server once

Like `orb-analysis`, this project reads **`ConnectionStrings:MarketData`**. The connection string is not stored in GitHub. Add a local **`appsettings.Development.json`** file next to the backtest project's `.csproj` (this filename is ignored by Git), using the **same SQL connection string that works in `orb-analysis`**:

```json
{
  "ConnectionStrings": {
    "MarketData": "PASTE_YOUR_WORKING_ORB_ANALYSIS_SQL_CONNECTION_STRING"
  }
}
```

Alternatively, store `ConnectionStrings:MarketData` using Visual Studio **Manage User Secrets**, or provide the environment variable `ConnectionStrings__MarketData`. Environment variables override local JSON; no login information is added to source control.

`appsettings.json` provides the input defaults:

```json
{
  "MarketData": {
    "Symbol": "",
    "TimestampTimeZone": "America/New_York"
  }
}
```

- **Table**: `dbo.MES_DATA` (historically misnamed; it stores **ES** contracts) with columns `ts_event`, `symbol`, `open`, `high`, `low`, `close`, `volume`. All matching rows are loaded in chronological order; the date range is whatever is actually available in SQL.
- **Symbol**: Empty by default. SQL contract symbols are `ESZ5`, `ESH6`, `ESM6`, `ESU6`, `ESZ6`, etc., not the literal `MES`. With an empty symbol, the loader picks the highest-volume regular-session symbol for each Eastern date and keeps that one contract for the day (no mixing contracts within a trading day). To use one specific symbol instead, set `MarketData:Symbol` to the exact value in the database. If it finds no rows, it prints available symbols and counts.
- **Time zone**: `America/New_York` interprets `ts_event` as Eastern wall-clock time, the way `orb-analysis` currently uses it for the 9:30 market open. **If your database actually stores UTC timestamps**, change `MarketData:TimestampTimeZone` to `UTC` before running; incorrect settings shift the opening range and invalidate the test.
- **Pricing**: Despite the table name, these are **ES candles**, so initial 1–2 ES contracts use actual ES OHLC, ES volume and $50/point. Optional Stage-6 **MES reversal** trades still use ES candles as a **price proxy**, with MES $5/point P&L. For overlapping contracts, the loader picks one active ES contract for each trading day, while retaining **all contract months' one-minute candles** for that contract's prior seven-day FVG/reference-level lookback. The daily winner is selected using full-session volume; this roll-selection choice is retrospective, not an execution-time contract selector.
- **Input coverage**: No date range, row limit, or 42-session cap is applied to the SQL query. The full available data range is loaded; normal RTH session processing requires a complete 15-minute opening range. The console shows **total SQL rows across contracts**, **selected active-contract rows**, complete RTH sessions, and the earliest/latest dates. 
- **Output**: All existing optimizer reports and trades are still written to `data/` as CSV/text **results**. Input candle CSV files are no longer needed.

The SQL query uses a read-only SELECT. It validates chronological timestamps, unique bars per chosen contract/date, OHLC, and numeric volume. If configuration is missing or no bars match, the program stops with a descriptive error rather than silently analyzing stale CSV data.

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

The bundled October 2025–October 2026 calendar has scheduled dates and ET times for **CPI, monthly employment/NFP, PPI, PCE, GDP, FOMC decisions and minutes, ISM manufacturing/services, JOLTS, retail sales, and ECI**. It uses verified schedule dates from [BLS](https://www.bls.gov/schedule/2026/), [BEA](https://www.bea.gov/news/schedule/full), [Federal Reserve](https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm), [ISM](https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/) and [Census](https://www.census.gov/retail/release_schedule.html).

The generated `data/optimizer_economic_calendar_2026.csv` lists the precise dates, event names, release times, and source URLs. **Anticipation of Fed moves** is represented by proximity to scheduled FOMC decisions, not an invented historical rate-cut probability. This version does not import point-in-time consensus forecasts, actual economic surprises, unscheduled headlines, or earnings releases. Release dates in the October 2025–October 2026 range include shutdown-adjusted 2025 dates rather than mechanically assuming monthly release days. If SQL history extends outside **October 6, 2025–October 30, 2026**, expand the embedded event calendar before relying on event-filter optimization. Otherwise dates without scheduled event records will be treated as non-event days; the console prints a warning whenever the SQL data exceeds the calendar window.

This runs the original Orb stage-based stop rules, **not** the previously tested $500/$450 profit-lock ladder.

## Results

The console displays the **best overall configuration based on the entire historical dataset** and its net P&L, trade counts, fees, drawdown and settings. Separately, it displays the exact training/test date ranges for each walk-forward fold, the selected training configuration, next-block test results, and the stitched **out-of-sample P&L**. The most recent fold winner is shown separately from the best overall winner.

New CSV/text reports are written to `data`:

- **`optimizer_full_dataset_best_summary.txt`** — best fixed configuration selected from the entire full-dataset grid, its net P&L, drawdown, all settings and number of qualifying candidates.
- **`optimizer_full_dataset_best_trades.csv`** — all trades and cumulative P&L for the full-dataset optimized winner.
- **`optimizer_walk_forward_folds.csv`** — each fold's selected settings, training results, immediately following test results, dates, trade counts, and full-dataset comparison metrics.
- **`optimizer_full_sample_comparison.csv`** — three rows, one per selected fold winner, with the fixed configuration's **full-dataset** net P&L, fees, trades, wins, drawdown, and its corresponding next-block test P&L.
- **`optimizer_full_sample_latest_config_trades.csv`** — every trade taken by the most recent fold winner when replayed across the *entire* dataset, including cumulative net P&L and its chronological period.

- **`optimizer_economic_calendar_2025_2026.csv`** — official historical release schedule used by the optimizer.
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
- ES initial trades use ES contract OHLC and ES volume from the SQL table; the backtest cannot reproduce exact market fills or intra-minute trade ordering. Optional MES Stage-6 trades are simulated using ES candle prices as a proxy with the MES $5-per-point multiplier. Treat all P&L as estimates.
- Estimated round-trip cost is **$10 per contract**, as in the prior simulation. The reported drawdown is calculated on **closed-trade equity**, not intratrade swings.
- The event-calendar policies are applied without using release outcomes, and train/test date boundaries remain chronological. The historical event calendar covers October 6, 2025–October 30, 2026 and is not a live feed.
- The optimizer evaluates the **same exhaustive 2,571,264-config grid** and writes the same output files, but avoids two major redundant operations: the first-entry signal is cached across risk/position/trailing variants (all of which share the same entry rules), and all three expanding-window training prefixes plus the full sample are aggregated **in one chronological pass** instead of four scans of overlapping days. Trades, costs, chronological ordering, drawdown, and fold ranking rules are unchanged. Progress is printed every 100 base configurations. The run may still take time because it tests the full grid, and stopping early leaves only a partial all-configurations CSV.

**Development status:** SQL Server candle loading is now the default start-up path. The historical-download code remains for reference but is no longer run by pressing Play. This environment cannot access your local SQL Server or execute a .NET build, so the new query and backtest must be verified in Visual Studio.
