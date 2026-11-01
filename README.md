# Backtest-for-new-orb

A **read-only** .NET 10 command-line downloader that uses the same TopstepX API as [Orb](https://github.com/BrandonTCozart/Orb). Downloads the previous **3 calendar months** of 1-minute **ES** (E-mini S&P 500, not MES) OHLCV candles and exports a single CSV.

## Security first

**Rotate your Topstep API key immediately.** The original Orb repository currently has an API key hard-coded in `TopstepModels.cs`, and that repository is public. Removing the key from the latest commit does not remove its exposure in Git history. This downloader never stores credentials in source control and cannot place orders.

## Run (PowerShell)

Requires .NET 10 SDK and your own TopstepX API access.

```powershell
$env:TOPSTEP_USERNAME="your-topstep-username"
$env:TOPSTEP_API_KEY="your-new-api-key"
dotnet run --project Backtest-for-new-orb.csproj -- --output data/es_1m_3months.csv
```

To specify exact start/end times in UTC:

```powershell
dotnet run -- --start 2026-08-01T00:00:00Z --end 2026-11-01T00:00:00Z --output data/es_aug_oct.csv
```

The program will try `/api/Contract/search` for the quarterly ES contracts and allow explicit Topstep contract IDs when expired ones cannot be looked up:

```powershell
dotnet run -- --contract ESU6=ACTUAL_ESU6_ID --contract ESZ6=ACTUAL_ESZ6_ID
```

Contract IDs must come from your Topstep account, not guessed IDs.

## How it works

- Authenticates through `/api/Auth/loginKey` using environment variables.
- Fetches 1-minute candles from `/api/History/retrieveBars` in seven-day windows, under the request limit of 20,000 candles.
- Uses an **approximate calendar-based quarterly roll** eight calendar days before the contract's third-Friday expiration. The resulting file is *not* back-adjusted continuous futures data.
- Deduplicates overlapping minute timestamps and sorts results ascending.
- Writes `timestamp_utc,contract,open,high,low,close,volume` to a CSV excluded from Git.
- Logs API errors and empty windows. By default it refuses to write a CSV after such errors; `--allow-partial` enables knowingly incomplete CSV output.

**Historical access is not guaranteed**: Topstep may restrict expired-contract candles or older minute data. The program cannot prove there are no missing individual minutes inside otherwise nonempty windows. Confirm session completeness and rolls before running a serious backtest. This has not been run against your Topstep account by ChatGPT.
