# ES Multi-Strategy Backtester + Original ORB Optimizer

Open **`Backtest for new orb.slnx`** in Visual Studio and press **Play**. The current default runs a **focused five-family** chronological research experiment: it selects from **235,008 base configurations (470,016 original/reversed directional evaluations)** on `dbo.MES_DATA_2020_2025`, freezes the training-selected regime-specific configurations and portfolio router, and evaluates them **unchanged** on `dbo.MES_DATA` (2025–2026). Set `Backtest:StrategyScope=Full` to restore the complete **19-family, 587,520-base-configuration (1,175,040-directional-evaluation)** search. Both SQL tables contain **ES** futures candles despite their legacy `MES` names. No live orders are placed and neither table is modified.


### Adaptive configurations and candle features (November 2026)

`Backtest:ConfigurationMode` (independent of `StrategyScope`) controls configuration selection:

- `Baseline`: original one training-selected configuration per strategy; no adaptive specialists.
- `Adaptive`: retain two diverse high-scoring TRAINING configurations per strategy/direction/exit class; select **at most one per strategy** in each predeclared `Trend`, `Range`, or `Transition` market state. Missing references, volatility shocks and thin liquidity are **no trade**.
- `AdaptiveCandles` (**default**): same as Adaptive, additionally compares an optional pre-entry **rejection-wick / engulfing candle confirmation** against unfiltered candidates on training only. Some selected experts may require a candle pattern; others may not.

The state is computed exclusively from completed, same-contract ES candles **at 09:45 morning / 13:00 afternoon / 19:00 overnight** using fixed, disclosed market-regime thresholds. The candle confirmation uses only the completed one-minute bar **immediately before next-bar-open entry**. An expert is eligible only after the observation cutoff, and only if its historical training sample satisfies minimum trade/half-sample counts, a profitable **$20/ES cost sensitivity**, a positive drawdown/consistency-adjusted score, and minimum profitable calendar months. Empty states explicitly **do not trade**. One frozen rule per family/state is selected from training; evaluation data never update that rule. `FOLD_EXPERT_COMPARE` prints baseline/adaptive/candle OOS research comparisons; `TRAIN_EXPERT` and `REPLAY_EXPERT` disclose frozen routes and filters. The existing portfolio router continues to rank families using their **training-only** adaptive P&L.

**Limitations:** This is a transparent coarse regime-conditioned configuration selector, not an AI model or real-time retrainer. It selects from a deliberately small top-candidate shortlist per strategy rather than claiming to find the absolute best setting for every regime across the entire million-candidate search. Historical selection still carries multiple-testing bias; the earlier data and 2025–2026 have already been examined. New untouched dates or paper trading are necessary for independent validation. Candle patterns are hypotheses, not assumed predictive edges.

Set `"ConfigurationMode": "Baseline"` to compare against the prior single-configuration implementation without changing the five-family shortlist.

### Five-family focused research (default)

`Backtest:StrategyScope` is independent of `Backtest:SearchProfile`:

- `Focused` (**default**): Opening Range Breakout (ORB), Morning VWAP Reversion, Afternoon VWAP Reversion, Afternoon Momentum, Morning Liquidity Sweep. Deep tests **235,008 base settings × two actual trade directions = 470,016 evaluations**.
- `Full`: all original **19 strategy families**, **587,520 Deep settings × two directions = 1,175,040 evaluations**.

Both modes retain the **exact original Deep-grid candidate IDs**; focused filtering happens *after* ID assignment. Only the selected five families enter ranking, fold comparisons, router optimization, final training selection, and frozen forward replay; the 14 other families are **not deleted**, and `Full` restores them. Frozen legacy `HistoricalReplay` remains an explicit, 19-family pinned comparison mode and is unaffected by `StrategyScope`. `Smoke` can be combined with either scope for development tests.

**Research caveat:** This shortlist was chosen after inspecting previous walk-forward and 2025–2026 results. Thus rerunning 2025–2026 with the focused list is **not** independent validation of the five-family selection. Use a later untouched period or live paper-trading observations to validate whether it generalizes. Smaller scope reduces simulations and multiple-testing burden but does not prove improved edge.

To change the default, edit `appsettings.json` under `Backtest`:

```json
{
  "Backtest": {
    "Mode": "HistoricalThenRecent",
    "SearchProfile": "Deep",
    "StrategyScope": "Focused",
    "ConfigurationMode": "AdaptiveCandles",
    "ParallelWorkers": 0
  }
}
```

Change only `StrategyScope` to `"Full"` for the original 19-strategy experiment.

### Consistency scoring and true reverse-direction research

The optimizer evaluates **original AND inverted signal directions for each enabled base grid configuration**, preserving all original config IDs (235,008 in the default Focused Deep scope; 587,520 in optional Full Deep). A reversed candidate retains the ORIGINAL signal's observation timestamp and next-open entry, but simulates its own side-specific initial stop, risk cap, target, trailing stop and time exit with the same estimated costs. ORB uses the actual staged ORB stop engine for both directions. For precomputed Fibonacci fixed targets, the reverse simulation reflects the **known pre-entry distance** across the next-open price; for structure-target exits, only actual opposite-side historic levels qualify. No backtest return is negated, and an overnight or same-day OOS result is never used to pick a direction. A selected strategy records `signal_mode=Original`, `Reversed`, or `Disabled` in its frozen parameters.

The existing training robustness formula (net P&L minus 0.5×drawdown minus 0.5×loss in either training half) now additionally penalizes (a) a low share of **profitable calendar months**, (b) a low share of **profitable calendar weeks**, and (c) domination by one profitable week. Inactive calendar periods are included in denominators; the trade-count floor and both-half coverage requirements remain unchanged. A family is **Disabled** if even its highest-scoring TRAINING original/reverse choice fails to produce both positive net and positive consistency-adjusted robustness. This creates an explicit no-trade alternative; disabled families cannot produce forward trades. A report row prints each chosen direction, its positive week/month counts, and the largest positive week's share of gross profitable weeks. The selection rule is deliberately simple, fixed in advance, and NOT fitted on 2025–2026 forward results.

Because the two sides require genuinely different price paths and stops, Deep research is expected to take **substantially longer** (roughly doubling the per-candidate simulation workload). Previous report totals and configuration selections are not directly comparable: rerun historical training, chronological walk-forward tests, and frozen forward replay. The 2025–2026 period has been repeatedly inspected and cannot be treated as a pristine, never-seen research holdout. A future new calendar period or paper-trading forward sample is required to establish real generalization.

### Point-in-time futures contracts and regime router (November 2026)

The SQL loader now removes the previous **full-day-volume contract selection lookahead** for automatic symbol choice. A trading session's ES contract is committed from the **most recent completed, contiguous 390-minute RTH session**, never from that session's future volume. Contract roll decisions take effect from **18:00 ET onward** after that day's RTH completes. Overnight (18:00 through next 09:29) and next-day regular trading retain the same as-of contract; if the selected contract is absent, the loader does not silently substitute the ex-post most active contract. The first source day is a bootstrap/observation day unless an explicit fixed symbol was supplied; stale reference windows are excluded. Raw SQL tables remain **read-only**. A fixed `MarketData:Symbol` still selects that explicitly known contract, with no automatic rolling.

The **RegimeAware** portfolio router competes against the existing single, fixed-session, and simple adaptive policies inside each walk-forward training fold. It uses three causally timestamped 1-minute-bar windows: **18:00–18:59 ET** (available 19:00) for overnight, **09:30–09:44 ET** (available 09:45) for morning, and **12:00–12:59 ET** (available 13:00) for afternoon. Volatility is compared against the corresponding window of the most recent **completed, same-contract** RTH/overnight session; relative traded volume is used as a **liquidity proxy, not a bid/ask or depth estimate**. Price directional efficiency and ATR-normalized movement distinguish trend from range. High volatility, thin relative volume, uncertain signals or insufficient prior reference trigger **no trade in that slot**. Candidates cannot enter before the regime observation becomes available.

Regime thresholds are selected exclusively from training history (small, disclosed 16-threshold grid, optionally with chronological fallbacks), with frozen choices evaluated on later OOS dates. The console reports `FOLD_REGIME_COMPARE` for each fold and `REPLAY_REGIME_COMPARE`, `REPLAY_REGIME_AUDIT`, `REPLAY_TOTAL,RegimeComparison`, plus weekly and yearly regime comparison rows in the later frozen evaluation. This reports a trained regime alternative even when another router wins the training selection. These improvements **change which sessions and contracts enter the backtest**: older performance numbers and previously frozen configuration IDs are **not directly comparable**, and real SQL re-optimization/replay is required. The synthetic suite tests future-volume roll avoidance, decision cutoffs, future-bar immunity, six regime states, and parallel/serial parity. No actual SQL outcomes have been measured by CI.

## Choose which research to run

Set `Backtest:Mode` in `appsettings.json` or a local override:

| Mode | What runs |
| --- | --- |
| `HistoricalThenRecent` **(current default)** | Five-fold diagnostic walk-forward on **2020–2025**; fit final strategy configurations and router on **all** validated older dates; freeze them and run forward on **2025–2026** without reoptimization |
| `ResearchThenHistorical` | Original reverse-date study: recent 2025–2026 walk-forward; take the last fold's training-selected settings and replay them on older 2020–2025 |
| `Multi` | Recent-data multi-strategy grid, walk-forward results, and router comparisons only |
| `HistoricalReplay` **(legacy)** | Previously pinned 19 historical configs from an older recent fold; no fresh optimization |
| `Orb` | Original separate exhaustive ORB-only optimizer |
| `Both` | Recent-data multi-strategy optimizer followed by independent ORB optimizer |

The full **Deep** multi-strategy grid contains **587,520 configurations across 19 strategy families** (default Focused runs five), retaining all original IDs and comparing fixed reward/risk targets, historical support/resistance targets, 25 ATR trailing activation/distance pairs with selected break-even choices, ORB-inspired staged profit locks, and target-activated trails. ORB itself retains its native staged exits. The `Smoke` profile (2,040 candidates) is a quick development/regression subset; default `Deep` tests everything. Running Deep on ~1,246 older validated sessions is substantially more computationally expensive than the previous ~250-session recent optimization.

### CPU parallel processing

The default `Backtest:ParallelWorkers: 0` means **automatic**: use up to **8 logical CPU cores**, retaining one for other work when possible. The original ORB candidates run sequentially because their signal/trade cache is shared; the **18 non-ORB families run in parallel**. Each worker owns isolated, bounded entry/exit caches, while candidate scores are merged under a short synchronization lock using the **same scoring and ID tie-breakers**. A synthetic regression test compares a complete single-worker Smoke run with a four-worker run and requires **identical configs, portfolio decisions and P&L**.

To override the worker count, set `"ParallelWorkers": 4` (for example) under `"Backtest"` in `appsettings.json`. Set **`1` to disable parallelism** for troubleshooting; use `0` to let the program choose. More workers can consume more memory and do not guarantee linear speedups. CPU parallelism starts only when running the **updated executable**; it cannot accelerate a backtest already underway, and the backtester cannot resume a half-completed Deep grid. No SQL changes are required.


**Training vs. testing:** In `HistoricalThenRecent`, the five chronological walk-forward folds **inside the older table** are useful internal diagnostics, but the eventual frozen configurations are chosen by a **final fit on every validated older session**, using training-period net P&L penalized for drawdown and imbalance between early/late halves, with minimum trade counts. The portfolio router is also selected using **older data only**. The `MES_DATA` table is loaded **only after** these choices are frozen. Data in the future/test table are never used to choose a strategy, exit, weekday policy, or router. The program refuses to label overlapping dates a forward holdout.

**Evidence caveat:** Both date ranges have already been reviewed in earlier project iterations, so **even the chronologically later 2025–2026 set is not a pristine, never-seen holdout for the research project**. This reversal is a useful diagnostic, not proof of an independently validated trading edge.

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
| **Support/resistance bounce**: bullish/bearish confirmation at historical support/resistance | Morning or afternoon (separately ranked) | 11:30 / 15:55 |
| **Support/resistance breakout**: close beyond a prior level with directional candle | Morning or afternoon (separately ranked) | 11:30 / 15:55 |
| **Breakout + retest**: completed breakout, then level hold and rejection on retest | Morning or afternoon (separately ranked) | 11:30 / 15:55 |
| **Fibonacci retracement**: 38.2%, 50%, 61.8% pullbacks of past-only swing range | Morning or afternoon (separately ranked) | 11:30 / 15:55 |
| **Fibonacci extension**: range breakout toward 127.2% or 161.8% projected extension | Morning or afternoon (separately ranked) | 11:30 / 15:55 |
| **Liquidity sweep / failed breakout**: wick beyond level, candle closes back inside | Morning or afternoon (separately ranked) | 11:30 / 15:55 |

### Fixed targets, historical levels, ATR trails and staged profit locks

Each **non-ORB** family compares these five exit modes on the same causally known entry signals. ORB retains its own separate staged engine.

- **FixedTarget**: Original ATR-sized initial stop and fixed reward/risk take-profit. For Fibonacci families, the fixed take-profit may be a previously completed swing or extension level, capped by the tested reward/risk value.
- **StructureTarget**: Exits at the nearest opposing historical ES level within **0.5R to 4R** of the entry: previous complete same-contract RTH high/low, completed overnight high/low (RTH trades only), the completed first 15-minute opening range (after 09:45 ET only), a previously completed Fibonacci reference, and the high/low from the **20 or 60 bars BEFORE entry**. No valid level means that particular variant skips the trade; there is no arbitrary fallback target and no future pivot/lookahead.
- **TrailingStop**: **25** pairs: activation at **0.5R, 1R, 1.5R, 2R or 3R** of favorable price movement and trailing distance **0.5, 1, 1.5, 2 or 3 ATR** (ATR fixed from entry). There are also four optional **cost-aware break-even** variants (1R/2R activation, 1/2 ATR trail) which raise the stop to one tick beyond entry after +1R favorable movement.
- **StagedProfitLock**: An ORB-inspired, risk-normalized model: at +0.5R the stop moves to entry, at +1R it locks +0.5R, and at +2R it trails from maximum favorable excursion by **0.5 or 1 ATR**. This does not reproduce ORB's absolute-dollar FVG stages exactly.
- **TargetThenTrail**: At **1.5R or 2R** favorable movement, the target becomes the **activation point for a 1 ATR trailing stop**, locking at least half the target milestone rather than exiting immediately.

All modes retain one ES, the **$600 initial stop-risk cap including $10 estimated round-trip costs**, tick rounding, the same session-based hard time exits, stop-first fills and worse opening-gap fills. New trailing levels use the completed minute's favorable extreme but become active only on the **next minute**. This enlarged search increases overfitting risk. The original `ResearchThenHistorical` mode retains the frozen final-fold approach; the new default `HistoricalThenRecent` freezes a full-older-period fit before testing on the strictly later data.

### Price-action reference rules

Support/resistance is defined from **one of four pre-entry sources**: (1) the completed 09:30–09:44 opening range, (2) the preceding **fully validated** regular session of the same ES contract within four calendar days, (3) the full verified overnight high/low, or (4) the preceding 20/40/60 completed one-minute highs and lows. Missing levels produce no signal for that candidate; no later-day or different-contract prices are substituted. For rolling levels, the current signal bar is excluded. Breakout/retest remembers the reference from the breakout candle, checks a completed 5- or 12-minute retest window, and requires the level to hold. Liquidity sweeps require a wick beyond the level followed by a confirmed close back inside before the next-bar entry.

Fibonacci anchors are **rolling past-only extrema** from 20/40/60 completed minutes, not zigzags or future-confirmed pivots. Chronological order of the extrema identifies up/down impulses; price must reject a specified retracement level or close beyond the old high/low toward a specified extension. Fixed-target Fibonacci trades use the historic swing/extension as a potential take-profit, **capped by the tested reward/risk limit**. Their trailing-exit counterparts instead use the tested ATR trailing distance and activation, without the fixed price target. Both use the same conservative ATR-derived initial ES stop/risk rules. Levels are in actual ES points (0.25-point tick rounding). These are precisely defined *testable hypotheses*, not claims that Fibonacci levels are intrinsically predictive.

Morning price-action signals run after the completed opening range through roughly 11:15 ET (with an optional delayed start); afternoon signals run approximately 13:00–15:05 ET. The 12 additional morning/afternoon families compete with the original seven for the training-selected **single-strategy, fixed-session, and adaptive-regime** portfolios. The adaptive router chooses its training-best *trend-following* or *reversal* strategy for each slot using only the prior overnight, 09:45 and 13:00 features. The fixed and adaptive portfolio searches now **compare versions with and without fallback strategies**: eligible alternatives are ranked by previous training robustness and are only considered within the same overnight/morning/afternoon slot. The **earliest eligible signal executes**, using training rank to break identical entry-time ties. This is causal: it never waits to discover a primary strategy failed by the end of the morning and then retroactively fills an earlier fallback. Only one strategy can trade per slot per date, even when multiple families signal. Standalone single-strategy test results remain unchanged by fallback routing.

The overnight trading session for a given regular-trading day starts at **18:00 ET the preceding calendar day**. New strategies use **one ES**, an ATR-derived initial stop capped at **$600 including assumed transaction cost**, and several tested hard take-profit OR trailing-exit, lookback, breakout/mean-reversion threshold, and economic-news parameters. The ORB baseline uses the original staged ORB exits, not the non-ORB fixed/trailing bracket implementation.

The event rules now use a historical 2020–2025 calendar alongside the 2025–2026 schedule: off, avoid major announcement dates, avoid intraday announcement windows and avoid FOMC plus prior trading day. Overnight tests omit the redundant intraday-window policy.

## Valid trading days and avoiding hindsight

Before testing, the runner validates that the US regular session contains **exactly 390 consecutive one-minute candles from 09:30 through 15:59 ET**, all for one ES contract. Any incomplete or mixed-contract RTH date is excluded from the entire multi-strategy comparison, not quietly filled with synthetic candles. Overnight strategies additionally require **all 930 consecutive one-minute candles from prior-day 18:00 through the current day's 09:29 ET** for the same contract. If overnight data are incomplete, the RTH date remains eligible for *morning and afternoon* strategies; only the overnight slot is unavailable. Audit counts and exclusions appear in the terminal without writing files. Regular early-close sessions are deliberately excluded.

The regular data run uses **five expanding-window chronological walk-forward folds**: the earliest 40% of valid sessions provides initial training, and the remaining 60% is tested in **five contiguous, nonoverlapping blocks**. With a tiny synthetic dataset (fewer than 60 sessions), the runner falls back to three folds. Within each fold, only the **earlier training dates** choose the strategy's configuration, best session-slot strategies, trend-versus-reversal candidates and adaptive routing thresholds; its immediately following test block is excluded from that selection. The dynamic router only uses features available **before** the corresponding trading session: overnight's first hour (by 19:00), opening-range width/volume/move (by 09:45), and the morning move/ATR (by 13:00). Single-strategy, fixed-session and adaptive-regime policies are compared, with training-ranked fallback variants for the two multi-strategy modes. The best *training* policy is then applied to the next test block. Per-fold console diagnostics also compare the training-selected best fallback router against the best original no-fallback router on the same OOS dates.

Deep evaluates one candidate at a time **without writing candidate-score files** and retains only selected leaders. For speed it caches entry signals and a **bounded set of simulated entry/exit paths** when candidates differ only in economic-event or weekday exclusions; those filters are applied afterward without reusing a candidate ID. It also calculates all five training folds, their two halves and whole-history results in **one chronological scoring pass per candidate**, rather than rescanning and sorting trades for each window. Both optimizations preserve the same simulation and ranking rules. It checks performance in both halves of each training period, requires enough trades in each half, and ranks by **training net P&L penalized for drawdown and losses concentrated in one half**. This makes the choice less dependent on a single exceptional period, although it does not eliminate overfitting. The adaptive-vs-fixed-vs-single portfolio search uses the same training-only robustness score, followed by chronological OOS testing.

The portfolio selects **at most one strategy per time slot**, never overlaps actual position timestamps, limits trading to **three sequential trades per trading date**, and stops placing new entries once prior trades that date have realized **-$1,000 or worse**. It does **not** cap the loss on a gap-through stop; that risk is reported by the simulated fill. ES contract rollovers are never stitched within a single trade. The model intentionally uses minute bars (not quote/tick data), and all results remain research estimates.

**Research caveat:** We have already examined this historical period while developing ORB. Walk-forward testing reduces within-fold hindsight, but this historical dataset is not wholly unexamined; comparing many new ideas on it can still overfit. Never confuse the in-sample best-configuration reports with future returns.

## Sequential 2020–2025 training and 2025–2026 forward evaluation (current Play mode)

With `Backtest:Mode=HistoricalThenRecent` and `Backtest:SearchProfile=Deep`, pressing **Play** automatically:

1. Loads `dbo.MES_DATA_2020_2025`, validates the historical 2020–2025 release calendar and full ES sessions, and evaluates **235,008 focused Deep configurations in both directions** (or all 587,520 in Full scope) on older history. It also prints five expanding walk-forward folds **within** the older period to diagnose training instability.
2. **Refits on the entire validated older period** (approximately **2020-09-08 through 2025-09-08**), choosing the highest **risk/drawdown-adjusted training score** per strategy, subject to minimum trades in each half, and chooses the router using the same robust scoring and older data. Reports each final candidate in `TRAIN_FROZEN` lines with exact parameters. These are **full-sample in-sample training choices**, not older-fold OOS winners.
3. Freezes the **five selected strategy configurations and the router** (or all 19 in Full scope), clears caches, then loads **`dbo.MES_DATA`** and evaluates those choices unchanged on the later period (roughly **2025-10-06 through 2026-10-05**). **No future-period profit, loss, exit, or other outcome is used to refit anything.**
4. Prints `REPLAY_ROUTER`, `REPLAY_TOTAL`, `YEARLY_REPLAY`, and `WEEKLY_REPLAY` for **forward** performance, with an additional non-selected fallback-router diagnostic. The forward `REPLAY_TOTAL` column `nonoverlap_forward_net_usd` equals total net and overlap is zero by construction. No research CSV or text files are created.

**Historical release coverage:** `Backtesting/HistoricalEconomicCalendar.g.cs` includes archived 2020–2025 scheduled releases (BLS, Federal Reserve, BEA, Census, plus some rule-derived ISM dates). The optimizer validates its historical calendar coverage **before** fitting. The newer period uses the checked-in 2025–2026 release schedule. Any overlapping training/test dates cause a hard failure. The rules use calendar timing, not future economic outcomes.

**Previous modes remain available:**
- `ResearchThenHistorical` still optimizes on the recent table and replays its **fifth-fold training** choices on the earlier table, unchanged from the preceding experiment.
- `HistoricalReplay` still reproduces the older **hard-coded** fold IDs rather than the current optimizer's winners.
- `Multi`, `Orb`, and `Both` retain their prior behavior.

**Limitations:** The older table contains partial 2020 and 2025 and historically has **1,246** complete RTH sessions and **987** complete overnight windows; actual counts are revalidated at runtime. The backtest uses one-minute OHLC fills, **$10 estimated ES round-trip costs**, a conservative stop-first convention and retrospective futures-roll selection. Changing the order of training/testing does not remove previous research exposure to the recent period or make backtest results equivalent to actual fills.

To return to the previous run, change `Backtest:Mode` to `"ResearchThenHistorical"`. To run only the recent optimizer, set `"Multi"`.

## Multi-strategy terminal reporting (no CSV output)

Setting `Backtest:Mode=Multi` evaluates the **same 587,520 Deep variants** and all five chronological walk-forward folds, but **does not create any CSV or text report files**. It prints session coverage, progress, per-fold winning configs and OOS scores, the original-versus-fallback router comparison, overall strategy P&L/win rate/max drawdown, portfolio summary/cost sensitivity, and in-sample maximum-profit configs. Existing report files from earlier runs are left untouched (they are not inputs).

At the end, the console prints graph-ready **weekly out-of-sample profit** for **every one of the 19 strategies and the selected portfolio**, with one line per strategy per week. Example (illustrative numbers):

```text
WEEKLY_OOS,week_start_et,week_end_et,strategy,net_usd,cumulative_net_usd,trades
WEEKLY_OOS,2026-08-31,2026-09-06,MorningMomentum,90.00,90.00,1
WEEKLY_OOS,2026-09-07,2026-09-13,MorningMomentum,-60.00,30.00,1
```

The console additionally prints one `TOTAL_PNL` row for **every strategy and the selected portfolio**, with three dollar columns: **earliest in-sample training-prefix P&L**, **nonoverlapping stitched out-of-sample P&L**, and **full-history total**, as well as full-history trade count, win rate and maximum closed-trade drawdown. The in-sample part uses the first fold's training-selected configuration on the initial 40% of sessions; the OOS part uses each fold's training-selected configuration on its subsequent test block. **Every historical date is counted exactly once**, even though later expanding training windows overlap earlier dates. This full-period result is different from the separately reported **maximum-net full-sample in-sample configuration**, which was optimized using all dates in hindsight.

Example (illustrative):
```text
TOTAL_PNL,strategy,in_sample_prefix_usd,out_of_sample_usd,total_usd,trades,win_rate_pct,max_closed_trade_drawdown_usd
TOTAL_PNL,MorningMomentum,1200.00,500.00,1700.00,23,43.48,800.00
```

Weeks use **Monday–Sunday Eastern calendar dates**, and overnight positions are attributed to their associated RTH trading date. The series cover the complete OOS test interval and explicitly show **zero** on weeks with no trades; cumulative P&L resets at the start of that OOS interval. Profits include the baseline estimated **$10 round-trip per ES**. Each week aggregates **only stitched next-block OOS trades** from each fold's *training-selected* strategy configuration (not the in-sample best result). The final series named `SelectedPortfolio` represents the actual non-overlapping, training-selected policy, not the sum of independently traded strategies. Paste the terminal output into ChatGPT to plot each strategy's weekly or cumulative curve.

**Note:** The separate legacy `Backtest:Mode=Orb` or `Both` exhaustive ORB runner still has its historical report output. `Multi` and the new default `HistoricalThenRecent` are terminal-only; you can bring their console output to ChatGPT for PDF and interactive HTML reports.

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
    "Mode": "HistoricalReplay",
    "SearchProfile": "Deep"
  }
}
```

- **Table**: `dbo.MES_DATA_2020_2025` for default full-historical optimization and `dbo.MES_DATA` for default recent forward testing (both contain **ES** contracts) with columns `ts_event`, `symbol`, `open`, `high`, `low`, `close`, `volume`. All matching rows are loaded in chronological order; the date range is whatever is actually available in SQL.
- **Symbol**: Empty by default. SQL contract symbols are `ESZ5`, `ESH6`, `ESM6`, `ESU6`, `ESZ6`, etc., not the literal `MES`. With an empty symbol, the loader picks the highest-volume regular-session symbol for each Eastern date and keeps that one contract for the day (no mixing contracts within a trading day). To use one specific symbol instead, set `MarketData:Symbol` to the exact value in the database. If it finds no rows, it prints available symbols and counts.
- **Time zone**: `America/New_York` interprets `ts_event` as Eastern wall-clock time, the way `orb-analysis` currently uses it for the 9:30 market open. **If your database actually stores UTC timestamps**, change `MarketData:TimestampTimeZone` to `UTC` before running; incorrect settings shift the opening range and invalidate the test.
- **Pricing**: Despite the table name, these are **ES candles**, so initial 1–2 ES contracts use actual ES OHLC, ES volume and $50/point. Optional Stage-6 **MES reversal** trades still use ES candles as a **price proxy**, with MES $5/point P&L. For overlapping contracts, the loader picks one active ES contract for each trading day, while retaining **all contract months' one-minute candles** for that contract's prior seven-day FVG/reference-level lookback. The daily winner is selected using full-session volume; this roll-selection choice is retrospective, not an execution-time contract selector.
- **Input coverage**: No date range, row limit, or 42-session cap is applied to the SQL query. The full available data range is loaded; normal RTH session processing requires a complete 15-minute opening range. The console shows **total SQL rows across contracts**, **selected active-contract rows**, complete RTH sessions, and the earliest/latest dates. 
- **Output**: `Multi` mode prints all results and dated OOS strategy series to the **terminal only**; no new CSV/text reports. The separate original `Orb` mode retains its legacy file reports. Input candle CSV files are not needed.

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

The bundled 2020–October 2026 calendar has scheduled dates and ET times for **CPI, monthly employment/NFP, PPI, PCE, GDP, FOMC decisions and minutes, ISM manufacturing/services, JOLTS, retail sales, and ECI**. It uses verified schedule dates from [BLS](https://www.bls.gov/schedule/2026/), [BEA](https://www.bea.gov/news/schedule/full), [Federal Reserve](https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm), [ISM](https://www.ismworld.org/supply-management-news-and-reports/reports/rob-report-calendar/) and [Census](https://www.census.gov/retail/release_schedule.html).

The generated `data/optimizer_economic_calendar_2026.csv` lists the precise dates, event names, release times, and source URLs. **Anticipation of Fed moves** is represented by proximity to scheduled FOMC decisions, not an invented historical rate-cut probability. This version does not import point-in-time consensus forecasts, actual economic surprises, unscheduled headlines, or earnings releases. Release dates in the October 2025–October 2026 range include shutdown-adjusted 2025 dates rather than mechanically assuming monthly release days. The archived calendar adds 2020–October 5, 2025 records. ISM early-year dates are **rule-derived** and need individual-date audit for unusual calendar exceptions. Data before 2020 or after October 30, 2026 are not covered by this static schedule. The historical replay fails closed if full years lack expected release classes.

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
- The event-calendar policies are applied without using release outcomes, and train/test date boundaries remain chronological. The event calendar covers January 2020–October 2026 and is not a live feed (2020–2025 data partially rule-derived for ISM).
- The optimizer evaluates the **same exhaustive 2,571,264-config grid** and writes the same output files, but avoids two major redundant operations: the first-entry signal is cached across risk/position/trailing variants (all of which share the same entry rules), and all three expanding-window training prefixes plus the full sample are aggregated **in one chronological pass** instead of four scans of overlapping days. Trades, costs, chronological ordering, drawdown, and fold ranking rules are unchanged. Progress is printed every 100 base configurations. The run may still take time because it tests the full grid, and stopping early leaves only a partial all-configurations CSV.

**Development status:** SQL Server candle loading is the default start-up path. The old download code remains for reference. Automated GitHub Actions compile and synthetic-check the new code; actual historical SQL backtest outputs must still be produced from your connected local SQL Server.
