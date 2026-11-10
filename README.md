# ES Multi-Strategy Backtester + Original ORB Optimizer

Open **`Backtest for new orb.slnx`** in Visual Studio and press **Play**. The program reads **ES futures one-minute candles from SQL Server `dbo.MES_DATA`** (the table name is legacy; the data are ES). By default it runs a **new seven-strategy comparative backtest**. No live orders, Topstep downloads or candle CSV inputs are used.

## Choose which research to run

Set `Backtest:Mode` in `appsettings.json` or a local override:

| Mode | What runs |
| --- | --- |
| `Multi` **(default)** | New multi-strategy grid, individual strategy walk-forward tests, and a strategy-routing portfolio |
| `Orb` | Original exhaustive 2,571,264-configuration ORB optimizer and its separate walk-forward reports |
| `Both` | Run multi-strategy first, then the original exhaustive ORB optimizer |

The default **Deep** multi-strategy search now evaluates **60,160 configurations across seven strategy families** (20,736 staged ORB configurations and 39,424 additional momentum/reversion configurations). The independent **original exhaustive ORB** search is still available under `Orb` or `Both` mode. Deep adds variations of entry windows, moving-average confirmation, VWAP filters, breakout buffers, RSI extremes, relative volume, ATR stops, targets, weekday restrictions, OR width, retests and economic-event rules. The lighter `Smoke` profile (416 candidates) exists only for quick regression/development tests.

Set `Backtest:SearchProfile` to `Deep` (default) or `Smoke` independently of `Backtest:Mode`. There is no automatic early cutoff or maximum runtime; Deep finishes its full disclosed grid before selecting the results.

## Strategies, trading hours and execution

All hours are **Eastern**. Every strategy uses **completed one-minute OHLCV bars**; signals are placed at bar close, entries simulated at the *next* bar's open, with the same estimated **$10 round-trip per ES** cost and a conservative **stop-first** assumption when stop and target touch inside one candle.

| Strategy | Entry research window | Hard time exit |
| --- | --- | --- |
| Original ORB with its actual staged stop logic | 09:45–10:00 or 11:00 cutoff | Original Orb time/stop rules (typically by 16:00) |
| Morning momentum (previous 10/20-bar breakout, EMA trend, VWAP, relative volume) | 09:45–11:00 | 11:30 |
| Morning VWAP mean reversion (distance from VWAP + RSI + reversal candle) | 10:00–11:15 | 11:30 |
| Afternoon momentum (fresh 10/20-bar breakout, EMA/VWAP and volume confirmation) | 13:00–15:00 | 15:55 |
| Afternoon VWAP mean reversion | 13:00–15:05 | 15:55 |
| Overnight momentum (breakout of first hour's high/low) | 19:00–21:00 preceding evening | 08:00 following morning |
| Overnight reversion (fade extremes beyond first-hour range) | 19:00–21:00 preceding evening | 08:00 following morning |

The overnight trading session for a given regular-trading day starts at **18:00 ET the preceding calendar day**. New strategies use **one ES**, an ATR-derived initial stop capped at **$600 including assumed transaction cost**, and several tested reward/risk, lookback, breakout/mean-reversion threshold, and economic-news parameters. The ORB baseline uses the original ORB exits, not these fixed bracket exits.

The event rules are reused from the 2025–2026 economic calendar: off, avoid major announcement dates, avoid intraday announcement windows and avoid FOMC plus prior trading day. Overnight tests omit the redundant intraday-window policy.

## Valid trading days and avoiding hindsight

Before testing, the runner validates that the US regular session contains **exactly 390 consecutive one-minute candles from 09:30 through 15:59 ET**, all for one ES contract. Any incomplete or mixed-contract RTH date is excluded from the entire multi-strategy comparison, not quietly filled with synthetic candles. Overnight strategies additionally require **all 930 consecutive one-minute candles from prior-day 18:00 through the current day's 09:29 ET** for the same contract. If overnight data are incomplete, the RTH date remains eligible for *morning and afternoon* strategies; only the overnight slot is unavailable. A dated audit is written to `data/multi_strategy_data_quality.csv`. Regular early-close sessions are deliberately excluded.

The regular data run uses **five expanding-window chronological walk-forward folds**: the earliest 40% of valid sessions provides initial training, and the remaining 60% is tested in **five contiguous, nonoverlapping blocks**. With a tiny synthetic dataset (fewer than 60 sessions), the runner falls back to three folds. Within each fold, only the **earlier training dates** choose the strategy's configuration, best session-slot strategy and adaptive routing thresholds; its immediately following test block is excluded from that selection. The dynamic router only uses features available **before** the corresponding trading session: overnight's first hour (by 19:00), opening-range width/volume/move (by 09:45), and the morning move/ATR (by 13:00). A fallback **single strategy** and a **fixed three-session schedule** are also compared with the adaptive router; the best *training* policy is then applied to the next test block.

Unlike the first implementation, Deep **streams each candidate's metrics to disk** rather than caching all of its trades in memory, and caches matching entry signals across different risk/target settings. It checks performance in both halves of each training period, requires enough trades in each half, and ranks by **training net P&L penalized for drawdown and losses concentrated in one half**. This makes the choice less dependent on a single exceptional period, although it does not eliminate overfitting. The adaptive-vs-fixed-vs-single portfolio search uses the same training-only robustness score, followed by chronological OOS testing.

The portfolio selects **at most one strategy per time slot**, never overlaps actual position timestamps, limits trading to **three sequential trades per trading date**, and stops placing new entries once prior trades that date have realized **-$1,000 or worse**. It does **not** cap the loss on a gap-through stop; that risk is reported by the simulated fill. ES contract rollovers are never stitched within a single trade. The model intentionally uses minute bars (not quote/tick data), and all results remain research estimates.

**Research caveat:** We have already examined this historical period while developing ORB. Walk-forward testing reduces within-fold hindsight, but this historical dataset is not wholly unexamined; comparing many new ideas on it can still overfit. Never confuse the in-sample best-configuration reports with future returns.

## New multi-strategy reports

Pressing Play in default `Multi` mode writes the following reports into `data/`:

- `multi_strategy_summary.txt` — training-selected portfolio out-of-sample P&L, per-fold results and each strategy's best full-history configuration, profitability, win rate and drawdown.
- `multi_strategy_full_sample_winners.csv` — highest-**net-profit** **in-sample** candidate for each of the seven strategies and the exact settings, net profit, win rate, drawdown, average trade and profit factor.
- `multi_strategy_full_sample_highest_win_rate.csv` — highest-**win-rate** candidate in each strategy family, using the same minimum trade-count requirement and reporting its **net profit**, costs, drawdown and settings; this is also **in-sample**.
- `multi_strategy_individual_walkforward.csv` — the **out-of-sample** result of each strategy's robust training-selected configuration on the next block, with results under **$10, $20 and $30 per-trade costs**.
- `multi_strategy_individual_oos_summary.csv` — **combined nonoverlapping walk-forward OOS results for every strategy family**, including net profit, win rate, drawdown, profitable fold count and higher-cost sensitivity. Ranking these after the run is descriptive, not a new trained selection rule.
- `multi_strategy_portfolio_walkforward.csv` — compare **single-strategy**, **fixed multi-session**, and **adaptive-regime** policies with per-fold training and out-of-sample results, training robustness score and higher-cost sensitivity.
- `multi_strategy_portfolio_trades.csv` and `multi_strategy_portfolio_monthly.csv` — selected portfolio trades, chronological P&L and monthly summaries.
- `multi_strategy_candidate_scores.csv` — every one of the 60,160 tested candidates with training fold and full-sample metrics, first/second-half net P&L, robustness ranking score and trade-count eligibility. This file streams as the grid runs; incomplete runs leave incomplete scores.
- `multi_strategy_data_quality.csv` — actual RTH and overnight minute counts, missing windows and excluded dates.

To compile and run synthetic checks without SQL: `dotnet run -- --self-test`. GitHub Actions also runs a .NET 10 build and these dependency-free tests on each push. For the full historical analysis, your local SQL connection is still required.

---

## Configure SQL Server once

Like `orb-analysis`, this project reads **`ConnectionStrings:MarketData`**. Put your working SQL connection string in **`appsettings.Local.json`** next to the `.csproj`, or in **Visual Studio User Secrets**. The local file is Git-ignored. The repository's existing `appsettings.Development.json` is a **tracked placeholder**, so do **not** put real credentials in that tracked file:

```json
{
  "ConnectionStrings": {
    "MarketData": "PASTE_YOUR_WORKING_ORB_ANALYSIS_SQL_CONNECTION_STRING"
  }
}
```

Alternatively, use **Manage User Secrets** or the environment variable `ConnectionStrings__MarketData`. Existing development config is still read for backward compatibility; the ignored `appsettings.Local.json` override is loaded after it. Environment variables have highest precedence. Never commit a real password.

`appsettings.json` provides the source and run-mode defaults:

```json
{
  "MarketData": {
    "Symbol": "",
    "TimestampTimeZone": "America/New_York"
  },
  "Backtest": {
    "Mode": "Multi",
    "SearchProfile": "Deep"
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

The optimizer separately selects **two full-dataset winners** from **all 2,571,264 candidates scored across 100% of the available historical sessions**. The **most profitable** winner is ranked by highest net P&L (then lower closed-trade drawdown, more ES trades and configuration ID). The **highest-win-rate** winner is ranked by winning trades / all completed simulated trades, using an exact ratio without rounding; ties go to higher net P&L, lower drawdown, more ES trades and configuration ID. Both must qualify with at least `max(4, ceiling(full sessions / 4))` first ES trades, to avoid a misleading winner with only a few trades. Win rate includes both ES and optional MES ES-price-proxy trades. Both rankings are **in-sample** and do not change or influence the three walk-forward fold winners.

The report shows **four different performance views**:

- **Best overall full-dataset configuration:** The highest-ranked qualifying setting from the entire 2,571,264-configuration search, evaluated on all complete opening-range sessions. Its full-sample net P&L, costs, trade counts, drawdown, individual settings and every trade are reported.
- **Highest full-dataset win rate:** The qualifying candidate with the greatest win percentage; reports its configuration, ES/MES-proxy trade count, net P&L after costs, and drawdown. This is also an in-sample selection, not independent validation.
- **Full-dataset fold-winner replays:** Separately replay the three configurations originally selected within the walk-forward training folds across all available sessions. **Do not add those totals**, because each covers the same period.
- **Walk-forward out-of-sample P&L:** Stitches the three immediately following test blocks together, applying the configuration selected for *each* fold. The three test blocks do not overlap.

For each fold, the console prints its **same-configuration full-dataset P&L** directly under that fold's next-block test P&L. The console also prints **BEST OVERALL CONFIGURATION** (by profit) and **HIGHEST WIN-RATE CONFIGURATION**, with each strategy's settings, win rate and full-dataset P&L, and a **FULL-DATASET RESULTS** comparison showing the three fold winners. The best overall configuration is not automatically the most recent fold winner, and the stitched out-of-sample result does not measure the full-dataset winner's own out-of-sample performance.

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

The console displays both the **highest-net-profit** configuration and the **highest-win-rate** configuration selected from the same historical dataset, including each strategy's net P&L, win rate, trade counts, fees, drawdown, and settings. Separately, it displays the exact training/test date ranges for each walk-forward fold, the selected training configuration, next-block test results, and the stitched **out-of-sample P&L**. The most recent fold winner is shown separately from the best overall winner.

New CSV/text reports are written to `data`:

- **`optimizer_full_dataset_best_summary.txt`** — best fixed configuration selected from the entire full-dataset grid, its net P&L, drawdown, all settings and number of qualifying candidates.
- **`optimizer_full_dataset_highest_win_rate_summary.txt`** — highest-win-rate qualifying configuration, its complete settings, win/trade count, net profit after estimated costs and drawdown. Its win rate includes ES plus optional MES proxy trades. Uses the same trade-count eligibility as the max-profit winner.
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

**Development status:** SQL Server candle loading is the default start-up path. The old download code remains for reference. Automated GitHub Actions compile and synthetic-check the new code; actual historical SQL backtest outputs must still be produced from your connected local SQL Server.
