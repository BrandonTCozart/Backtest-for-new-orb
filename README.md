# Backtest-for-new-orb

Run a **local 1-minute ES ORB backtest** by opening **`Backtest for new orb.slnx`** in Visual Studio and pressing the green **Start** button. The app **does not download data, contact Topstep, require user secrets, or place trades**.

## Input

Put the previously downloaded CSV in your local project folder's `data` directory:

- `data/es_1m_3months.csv`, or
- `data/es_1m_3months_INCOMPLETE.csv`

If both exist, the most recently modified file is used. The CSV header must be `timestamp_utc,contract,open,high,low,close,volume`. The downloaded candles are gitignored: they will stay on your PC, not in GitHub. If the program cannot find the file, its console output displays the expected folder. No login or credentials are required.

## What is tested

The **first ES trade per available trading day**, using the original `Orb` strategy code copied into `Backtesting/OrbStrategy.cs`:

1. Create the opening range from all 15 minute candles, **9:30–9:45 AM Eastern**.
2. Find the first candle **closing above OR high (long)** or **below OR low (short)**. Approximate a market fill at the **next candle's opening price**; skip days where the opening bar is missing.
3. Trade **one ES contract**. Calculate the initial stop with the same 75%-OR stop, $600 max estimated initial risk, and $500 fallback as `Orb`. The calculation uses the same $10 commission/slippage risk allowance.
4. Simulate the original Stages **3, 4 and 5**, reference levels, FVG-related stop adjustments, and stop-loss exits. This backtest **excludes the optional MES Stage 6 reversal trade**.
5. Close remaining positions at **4:00 PM ET**.

**Metric: Maximum Favorable Excursion (MFE)** means the maximum price movement in the trade's favor **between its entry and exit**, even if price subsequently reverses. For a long, it is the highest favorable high minus entry; for a short, entry minus the lowest favorable low. A trade that never moves in favor has **0** MFE. All completed first trades, including losing trades, are included in the average.

One ES point is **$50** for one contract, so the console prints average MFE in **points and equivalent dollars**, plus median, minimum/maximum and averages for long/short trades.

## Outputs (written automatically)

- `data/backtest_first_trade_mfe.csv` — one row for every completed first trade, with signal/entry/exit timestamps, prices, exit reason, MFE points, MFE dollars, actual exit price, realized points, and last stage reached.
- `data/backtest_skipped_days.csv` — sessions without a valid completed trade (missing bars, missing full opening range, no breakout, etc.).

## Important simulation limitations

- The original live `Orb` worker evaluates stops and trade stages from **individual quote updates**, but the downloaded history is **only 1-minute OHLCV**. This simulator uses **candle-close checks to adjust stops**, which become active on the next minute. Consequently it can produce different exits than live trading. It is a **bar-level approximation**, not a tick-perfect execution replay.
- When a protective stop is touched anywhere within a candle, the simulator conservatively assumes that the stop executes **before that candle's favorable high/low**. It counts any favorable opening gap but excludes the unknown intrabar favorable wick. If the candle opens beyond a stop, it uses the opening price as the simulated stop fill. Prices, fills, latency, fees, and slippage may differ from live execution.
- A downloaded file named `INCOMPLETE` is still accepted, but analysis covers **only the available sessions**. Missing 1-minute data during a trade causes that day to be skipped; missing entire dates cannot be detected from a file alone. Missing premarket/prior-day history can prevent reference levels from being available.
- Contract rollovers are taken from the **contract labels in your original CSV**; the data is not back-adjusted.
- The CSV is a study of trade movement, **not a profit forecast or investment recommendation**.

This app preserves the original `Orb` repository unchanged and never places trades.

**Development status:** The source was committed to GitHub; it has not been compiled or executed with your local candle CSV by ChatGPT.
