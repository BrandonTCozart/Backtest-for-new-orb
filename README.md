# Backtest-for-new-orb

A **Visual Studio 2022 .NET 10 console app** that downloads the previous **3 calendar months of 1-minute ES (E-mini S&P 500) candles** from TopstepX into a CSV. Open the solution and press **Start (green Play arrow)**. No terminal or command line required.

## First-time setup (inside Visual Studio)

1. Open **`Backtest-for-new-orb.sln`** in Visual Studio.
2. In **Solution Explorer**, right-click the **Backtest-for-new-orb project** (not the solution) and choose **Manage User Secrets**.
3. Replace the contents of the `secrets.json` editor with the following, using your *own newly rotated* API credentials:

```json
{
  "Topstep": {
    "UserName": "YOUR_TOPSTEP_LOGIN",
    "ApiKey": "YOUR_NEW_TOPSTEP_API_KEY"
  }
}
```

4. Save the secrets file. Set the **Backtest-for-new-orb** project as the startup project if Visual Studio asks. Choose the **Download 3 Months ES CSV** launch profile and click the **green Start arrow** (or press **F5**).

That's it: **each subsequent Start downloads a fresh trailing 3-month CSV automatically**. You do not need to use PowerShell or supply arguments. Console progress appears while downloading.

The output appears in the solution folder at:

```text
data/es_1m_3months.csv
```

The CSV columns are: `timestamp_utc,contract,open,high,low,close,volume`.

For development, Visual Studio stores user secrets on your PC, not in the GitHub repository. The downloader also accepts existing `TOPSTEP_USERNAME` and `TOPSTEP_API_KEY` environment variables as an alternative.

## Security warning

The original [Orb](https://github.com/BrandonTCozart/Orb) repo was publicly visible with a **hard-coded API key** in `TopstepModels.cs`. **Revoke that key and generate a new one immediately**; removing it from the latest source alone does not remove it from Git history. Never paste the real key into this repository or `launchSettings.json`.

## What the downloader does

- Logs in to TopstepX with `/api/Auth/loginKey`, based on the existing `Orb` integration.
- Uses `/api/Contract/search` to resolve ES quarterly futures contracts.
- Fetches 1-minute OHLCV candles via `/api/History/retrieveBars` in 7-day requests, under the 20,000-bar limit.
- Changes contracts at a **calendar-based approximation** of rollover (eight days before each quarterly contract's third-Friday expiration), not an exchange-provided or volume-based continuous series.
- Sorts bars chronologically and deduplicates minutes.
- Logs retrieval errors and refuses to write a misleading CSV if a request fails or returns an empty window. A previously generated CSV is not changed in that case. `--allow-partial` exists for advanced troubleshooting only.
- Makes **no trading/order requests**.

**Data coverage caveats:** Some expired contracts may not resolve through the API, and Topstep may not supply three months of 1-minute history. The program will report unresolved symbols; the exact contract ID can be supplied with `--contract ESU6=ID` if needed. Empty windows are flagged, but an individual missing minute inside a nonempty window is not automatically detected. Closed market sessions are normal. The stitched CSV is not back-adjusted. Validate rollover and gap coverage before relying on the backtest.

The code has been committed, but it has **not** been built or authenticated against your Topstep account here.
