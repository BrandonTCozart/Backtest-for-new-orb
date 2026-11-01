# Backtest-for-new-orb

A **Visual Studio 2022 .NET 10 console app** that downloads the previous **3 calendar months of 1-minute ES (E-mini S&P 500) candles** from TopstepX into a CSV. Open the solution and press **Start (green Play arrow)**. No terminal or command line required.

## First-time setup (inside Visual Studio)

1. Open **`Backtest for new orb.slnx`** in Visual Studio.
2. In **Solution Explorer**, right-click the **Backtest for new orb project** (not the solution) and choose **Manage User Secrets**.
3. Replace the contents of the `secrets.json` editor with the following, using your *own newly rotated* API credentials:

```json
{
  "Topstep": {
    "UserName": "YOUR_TOPSTEP_LOGIN",
    "ApiKey": "YOUR_NEW_TOPSTEP_API_KEY"
  }
}
```

4. Save the secrets file. Set the **Backtest for new orb** project as the startup project if Visual Studio asks. Choose the **Download 3 Months ES CSV** launch profile and click the **green Start arrow** (or press **F5**).

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
- Resolves ES quarterly futures contracts using `/api/Contract/searchById` first (including inactive expired IDs such as `CON.F.US.EP.U26`), then falls back to `/api/Contract/search`. The ID is verified as an ES contract before use.
- Fetches 1-minute OHLCV candles via `/api/History/retrieveBars` in 7-day requests, under the 20,000-bar limit.
- Changes contracts at a **calendar-based approximation** of rollover (eight days before each quarterly contract's third-Friday expiration), not an exchange-provided or volume-based continuous series.
- Sorts bars chronologically and deduplicates minutes.
- Logs retrieval errors and refuses to write a misleading CSV if a request fails or returns an empty window. A previously generated CSV is not changed in that case. `--allow-partial` exists for advanced troubleshooting only.
- Makes **no trading/order requests**.

**Data coverage caveats:** Expired contracts may resolve successfully through the API but Topstep may no longer supply their 1-minute bars. Independent ProjectX client documentation reports that its gateway does not retain sub-hour history for expired contract months. The program must actually fetch and verify the bars; successful contract ID resolution alone is not proof of three-month data availability. The program will report unresolved symbols; the exact contract ID can be supplied with `--contract ESU6=ID` if needed. Empty windows are flagged, but an individual missing minute inside a nonempty window is not automatically detected. Closed market sessions are normal. The stitched CSV is not back-adjusted. Validate rollover and gap coverage before relying on the backtest.

The code has been committed, but it has **not** been built or authenticated against your Topstep account here.


## Why ESU6 was missing

The first run downloaded ESZ6 but `/api/Contract/search` did not return ESU6. The program now tries the documented `/api/Contract/searchById` operation first with the ES contract ID convention, verifying the returned contract name and symbol. No manual ID entry is required for this attempt. If it resolves but the history endpoint returns no 1-minute bars, that is a provider data limitation rather than a login problem. The program does **not** label an incomplete history as a complete three-month CSV.

Sources:
- https://gateway.docs.projectx.com/docs/api-reference/market-data/search-contracts-by-id/
- https://gateway.docs.projectx.com/docs/api-reference/market-data/retrieve-bars/
- https://texascoding.github.io/project-x-py/api/client.html#contract-rolls-and-gateway-limits
