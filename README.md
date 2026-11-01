# Backtest-for-new-orb — ES P&L with $500 profit-lock ladder

Open **`Backtest for new orb.slnx`** in Visual Studio and press the green **Start** button. The app reads an existing local ES 1-minute CSV and runs a historical backtest. It **does not download candles, connect to Topstep, require credentials, or submit orders**.

## Input

Place one of these files in the project's local `data` folder:

- `data/es_1m_3months.csv`
- `data/es_1m_3months_INCOMPLETE.csv`

If both exist, the newest modified file is selected. The file must use columns `timestamp_utc,contract,open,high,low,close,volume`. If you previously downloaded an incomplete file, only those available sessions are tested; skipped days are reported.

## Strategy

This backtests the **first ES trade each trading day**, using the **unchanged** `Orb` strategy implementation copied to `Backtesting/OrbStrategy.cs`. It preserves the 9:30–9:45 AM Eastern opening range, long/short close-outside-range signal, 1 ES contract, original initial stop (75% of range with $600 estimated maximum risk and $500 fallback) and the original protective stop stages before the new target triggers. It excludes optional Stage 6 MES reversals. Any position still open at 4 PM Eastern is closed.

**New profit handling (gross unrealized dollars for one ES, $50 per point):**

| Maximum favorable profit reached | New stop protects |
| ---: | ---: |
| Below $500 | Existing ORB stop rules |
| $500 | $450 |
| $700 | $650 |
| $900 | $850 |
| $1,100 | $1,050 |
| And so on | $50 behind each $200 milestone |

This is **not a fixed $500 take-profit exit**. After reaching +$500, the trade continues; it exits when its protective stop is hit or at session close. Existing stops are never loosened. Once the +$500 milestone is reached, the ladder replaces the normal later-stage stop logic. The thresholds and amounts are in `Backtesting/ProfitLockLadder.cs`.

The backtest estimates **$10 round-trip cost per ES contract** (the same allowance used in the original stop-risk calculation). For example, if a stop fills at +$450, the estimated **net realized P&L is +$440**. This is a modeling assumption; actual commissions, fees and slippage can differ.

## Results

Visual Studio console output includes:

- **Total gross P&L, estimated costs and total net P&L** for the downloaded period
- Number of completed trades, winning/losing trades, win rate, average net P&L/trade
- Maximum drawdown calculated from *closed-trade cumulative equity* (not unrealized intratrade drawdown)
- Number of trades that reached the +$500 milestone and average maximum favorable movement (MFE)
- Separate long/short net P&L

The program writes these CSVs to your local `data` folder:

- `backtest_first_trade_500_lock_pnl.csv` — every trade's entry, exit, stop, gross, estimated fees, **net P&L**, cumulative net P&L, MFE, target milestone, and exit reason
- `backtest_monthly_500_lock_pnl.csv` — monthly P&L and monthly cumulative net P&L
- `backtest_skipped_days.csv` — sessions excluded due to missing or unusable minute data

All data CSVs are ignored by Git.

## Important limitations

**This is an approximation with 1-minute OHLC candles, not a tick-by-tick replay.** The live `Orb` worker updates stops from individual quotes and confirms orders with Topstep. This backtest instead uses the next candle's open for a simulated market entry, candle closes for the pre-$500 stage changes, and candle high/low to detect the new $500/$700/$900 milestones.

When the *previously active* protective stop and a favorable target are both touched inside one candle, the simulator conservatively assumes the **old stop happened first**. When a new ladder stop is raised and that same candle also touches the new stop, it assumes the **new stop was hit** and records the exit reason `ladder-stop-same-bar-assumed`. Intraminute price order is unknowable from OHLC alone. New ladder stops may trigger before the candle close in the model, unlike the prior stages. These assumptions can change both win rate and total P&L.

Other limits: fills at stop prices except when price gaps past an already-active stop (uses candle open); no real broker execution delays, commission variance, or slippage simulation; skip sessions with missing candles during an open trade; incomplete historical coverage and front-month roll selection are inherited from your downloaded data. A strategy that looks profitable in historical candles may not be profitable live.

**Development status:** Changes are committed to GitHub, but ChatGPT cannot access the CSV on your computer or execute the .NET project in your Visual Studio environment. The actual P&L figures will be computed when you press Start.

The live `Orb` repository was not modified.
