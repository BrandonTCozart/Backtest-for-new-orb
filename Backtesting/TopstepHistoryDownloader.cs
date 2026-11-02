using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Orb;

namespace Backtest_for_new_orb;

/// <summary>
/// Read-only TopstepX/ProjectX ES 1-minute history downloader.
/// Keeps the original CSV and all optimization code; never calls order endpoints.
/// </summary>
internal sealed class TopstepHistoryDownloader : IDisposable
{
    public const string OutputName = "es_1m_max_available.csv";
    private const string ApiUrl = "https://api.topstepx.com";
    private const int BarsPerRequest = 20_000;
    private const int ConsecutiveEmptyQuartersToStop = 8;
    private static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    private readonly HttpClient _http = new() { BaseAddress = new Uri(ApiUrl), Timeout = TimeSpan.FromMinutes(2) };
    private string? _token;
    private DateTimeOffset _lastHistoryRequest;

    private sealed record Bar(DateTimeOffset T, string Contract, decimal O, decimal H, decimal L, decimal C, long V);
    private sealed record Contract(string Id, string Name, string SymbolId, bool ActiveContract, decimal TickSize, decimal TickValue);

    public static async Task<string?> RunAsync(string directory, CancellationToken cancellationToken = default)
    {
        var secrets = new ConfigurationBuilder()
            .AddUserSecrets<TopstepHistoryDownloader>(optional: true)
            .Build();
        var user = FirstNonempty(
            Environment.GetEnvironmentVariable("TOPSTEP_USERNAME"),
            Environment.GetEnvironmentVariable("Topstep__UserName"), secrets["Topstep:UserName"]);
        var key = FirstNonempty(
            Environment.GetEnvironmentVariable("TOPSTEP_API_KEY"),
            Environment.GetEnvironmentVariable("Topstep__ApiKey"), secrets["Topstep:ApiKey"]);

        if (user is null || key is null)
        {
            Console.WriteLine("Topstep history download: credentials not configured. Configure Visual Studio User Secrets (Topstep:UserName and Topstep:ApiKey) or TOPSTEP_USERNAME / TOPSTEP_API_KEY environment variables.");
            Console.WriteLine("Existing historical CSV is unchanged; proceeding with local data.");
            return null;
        }

        Directory.CreateDirectory(directory);
        using var downloader = new TopstepHistoryDownloader();
        return await downloader.DownloadAsync(directory, user, key, cancellationToken);
    }

    private static string? FirstNonempty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private async Task<string?> DownloadAsync(string directory, string user, string key, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("TOPSTEPX ES 1-MINUTE HISTORY DOWNLOAD");
        Console.WriteLine("Requesting available quarterly ES contracts and 1-minute bars (up to 20,000 bars/request).");
        await LoginAsync(user, key, cancellationToken);

        var activeReply = await PostAsync("/api/Contract/available",
            new { live = false }, cancellationToken);
        var active = ReadContracts(activeReply).FirstOrDefault(c =>
            c.ActiveContract && c.SymbolId == "F.US.EP" &&
            c.TickSize == 0.25m && c.TickValue == 12.5m);
        if (active is null)
        {
            Console.WriteLine("No active standard ES futures contract was returned; existing CSV remains unchanged.");
            return null;
        }

        var outputPath = Path.Combine(directory, OutputName);
        var statePath = Path.Combine(directory, "es_1m_max_available.backfill-complete");
        var seedPath = File.Exists(outputPath) ? outputPath :
            File.Exists(Path.Combine(directory, "es_1m_3months.csv")) ?
                Path.Combine(directory, "es_1m_3months.csv") :
            File.Exists(Path.Combine(directory, "es_1m_3months_INCOMPLETE.csv")) ?
                Path.Combine(directory, "es_1m_3months_INCOMPLETE.csv") : null;

        var combined = new SortedDictionary<DateTimeOffset, Bar>();
        if (seedPath is not null)
        {
            foreach (var old in BacktestRunner.Load(seedPath))
                combined[old.T] = new Bar(old.T, old.Contract, old.C.O, old.C.H,
                    old.C.L, old.C.C, old.C.V);
            Console.WriteLine("Preserving " + combined.Count.ToString("N0", CI) +
                " existing candles from " + Path.GetFileName(seedPath));
        }

        // The first run scans backwards across quarterly contracts. Later
        // starts refresh only the recent history when the first scan completed.
        bool incrementOnly = File.Exists(outputPath) && File.Exists(statePath) && combined.Count > 0;
        var refreshFrom = incrementOnly ? combined.Keys.Last().AddDays(-5) :
            new DateTimeOffset(1997, 9, 1, 0, 0, 0, TimeSpan.Zero);
        if (incrementOnly)
            Console.WriteLine("Refreshing new/recent history from " +
                refreshFrom.ToString("yyyy-MM-dd", CI) + " (prior backfill already scanned).");

        var newest = DateTimeOffset.UtcNow;
        var retrievedCount = 0;
        var successfulContracts = 0;
        var missedQuarters = 0;
        var errors = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (year, month) in QuartersDescending(newest.Year + 1))
        {
            var contractStart = RollDateUtc(PreviousQuarter(year, month));
            var lower = contractStart > refreshFrom ? contractStart : refreshFrom;
            var upper = Math.Min(RollDateUtc(year, month), newest);
            if (upper <= lower) continue;

            var generatedId = "CON.F.US.EP." + QuarterCode(month) +
                (year % 100).ToString("D2", CI);
            var contractId = string.Equals(active.Id, generatedId, StringComparison.OrdinalIgnoreCase)
                ? active.Id : generatedId;
            if (!seenIds.Add(contractId)) continue;

            // Query historical IDs directly; some older contracts can have bars
            // even when they no longer appear in the contract-search directory.
            int quarterBars = 0;
            bool quarterFailed = false;
            // Twelve calendar days contain at most 17,280 one-minute bars,
            // staying below the 20,000-bar API response limit.
            for (var start = lower; start < upper;)
            {
                var end = Min(start.AddDays(12), upper);
                try
                {
                    var reply = await PostAsync("/api/History/retrieveBars",
                        new {
                            contractId, live = false, startTime = start,
                            endTime = end, unit = 2, unitNumber = 1,
                            limit = BarsPerRequest, includePartialBar = false
                        }, cancellationToken, allowBusinessFailure: true, history: true);
                    if (!IsSuccess(reply))
                    {
                        quarterFailed = true;
                        break;
                    }
                    if (reply.TryGetProperty("bars", out var bars) &&
                        bars.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in bars.EnumerateArray())
                        {
                            var at = item.GetProperty("t").GetDateTimeOffset().ToUniversalTime();
                            if (at < lower || at >= upper) continue;
                            var candle = new Bar(at, contractId,
                                item.GetProperty("o").GetDecimal(),
                                item.GetProperty("h").GetDecimal(),
                                item.GetProperty("l").GetDecimal(),
                                item.GetProperty("c").GetDecimal(),
                                item.GetProperty("v").GetInt64());
                            combined[at] = candle;
                            quarterBars++;
                        }
                    }
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                {
                    quarterFailed = true;
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
                {
                    Console.WriteLine("History request failed for " + contractId +
                        ": " + ex.Message);
                    errors++;
                    quarterFailed = true;
                    break;
                }
                start = end;
            }

            if (quarterBars > 0)
            {
                successfulContracts++;
                retrievedCount += quarterBars;
                missedQuarters = 0;
                Console.WriteLine(contractId + ": " + quarterBars.ToString("N0", CI) +
                    " candles; " + lower.ToString("yyyy-MM-dd", CI) +
                    " to " + upper.ToString("yyyy-MM-dd", CI));
            }
            else
            {
                missedQuarters++;
                if (quarterFailed)
                    Console.WriteLine(contractId + ": historical minute bars unavailable.");
                if (missedQuarters >= ConsecutiveEmptyQuartersToStop) break;
            }

            if (incrementOnly && upper <= refreshFrom) break;
        }

        if (combined.Count == 0)
        {
            Console.WriteLine("No historical ES candles available; no CSV was replaced.");
            return null;
        }

        // One contract per Eastern calendar day. Prefer the contract having
        // the most regular-session minute bars, so old and new CSV histories
        // cannot accidentally mix contract months during a trading session.
        var preferred = combined.Values
            .GroupBy(b => DateOnly.FromDateTime(MarketTime.ToEastern(b.T).Date))
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(b => b.Contract, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(cg => cg.Count(b => {
                        var et = MarketTime.ToEastern(b.T).TimeOfDay;
                        return et >= new TimeSpan(9, 30, 0) &&
                               et < new TimeSpan(16, 0, 0);
                    }))
                    .ThenByDescending(cg => cg.Count())
                    .First().Key);
        var final = combined.Values.Where(b =>
            string.Equals(b.Contract,
                preferred[DateOnly.FromDateTime(MarketTime.ToEastern(b.T).Date)],
                StringComparison.OrdinalIgnoreCase)).ToArray();

        var temp = outputPath + ".tmp";
        await using (var writer = new StreamWriter(temp, false))
        {
            await writer.WriteLineAsync("timestamp_utc,contract,open,high,low,close,volume");
            foreach (var bar in final)
            {
                await writer.WriteLineAsync(string.Join(",",
                    bar.T.ToUniversalTime().ToString("O", CI),
                    bar.Contract,
                    bar.O.ToString(CI), bar.H.ToString(CI),
                    bar.L.ToString(CI), bar.C.ToString(CI),
                    bar.V.ToString(CI)));
            }
        }
        File.Move(temp, outputPath, overwrite: true);
        if (errors == 0)
            await File.WriteAllTextAsync(statePath, "Historical contract search completed. Delete this marker to rescan old quarters.", cancellationToken);
        else if (File.Exists(statePath))
            File.Delete(statePath);

        Console.WriteLine("Saved: " + outputPath);
        Console.WriteLine("Total ES candles: " + final.Length.ToString("N0", CI) +
            "; first " + final[0].T.ToString("yyyy-MM-dd", CI) +
            "; last " + final[^1].T.ToString("yyyy-MM-dd", CI));
        Console.WriteLine("Contracts with newly retrieved minute data: " + successfulContracts +
            "; retrieved bar records: " + retrievedCount.ToString("N0", CI));
        if (errors != 0) Console.WriteLine("Some history requests failed; a later run will retry the historical scan.");
        Console.WriteLine("Original three-month CSV has not been changed.");
        Console.WriteLine();
        return outputPath;
    }

    private async Task LoginAsync(string user, string key, CancellationToken ct)
    {
        var reply = await PostAsync("/api/Auth/loginKey",
            new { userName = user, apiKey = key }, ct, authenticated: false);
        if (!IsSuccess(reply) || !reply.TryGetProperty("token", out var token) ||
            string.IsNullOrWhiteSpace(token.GetString()))
            throw new InvalidOperationException("TopstepX API-key login failed.");
        _token = token.GetString();
    }

    private async Task<JsonElement> PostAsync(
        string path, object payload, CancellationToken ct,
        bool authenticated = true, bool allowBusinessFailure = false, bool history = false)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (history)
            {
                var delay = TimeSpan.FromMilliseconds(650) -
                    (DateTimeOffset.UtcNow - _lastHistoryRequest);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                _lastHistoryRequest = DateTimeOffset.UtcNow;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, path) {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            if (authenticated && _token is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500)
            {
                if (attempt == 7) response.EnsureSuccessStatusCode();
                var delay = response.Headers.RetryAfter?.Delta ??
                    TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)));
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(2), ct);
                continue;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct));
            var result = document.RootElement.Clone();
            if (!allowBusinessFailure && !IsSuccess(result))
                throw new InvalidOperationException("TopstepX " + path +
                    " reported an API error: " +
                    (result.TryGetProperty("errorMessage", out var message) ?
                        message.ToString() : "unknown"));
            return result;
        }
        throw new HttpRequestException("TopstepX API retries exhausted: " + path);
    }

    private static bool IsSuccess(JsonElement element) =>
        element.TryGetProperty("success", out var property) &&
        property.ValueKind == JsonValueKind.True;

    private static List<Contract> ReadContracts(JsonElement element)
    {
        var result = new List<Contract>();
        if (element.TryGetProperty("contracts", out var contracts) &&
            contracts.ValueKind == JsonValueKind.Array)
            foreach (var item in contracts.EnumerateArray())
                result.Add(new Contract(
                    item.GetProperty("id").GetString() ?? "",
                    item.GetProperty("name").GetString() ?? "",
                    item.GetProperty("symbolId").GetString() ?? "",
                    item.GetProperty("activeContract").GetBoolean(),
                    item.GetProperty("tickSize").GetDecimal(),
                    item.GetProperty("tickValue").GetDecimal()));
        return result;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static IEnumerable<(int Year, int Month)> QuartersDescending(int startingYear)
    {
        for (var year = startingYear; year >= 1997; year--)
            foreach (var month in new[] { 12, 9, 6, 3 })
                if (year > 1997 || month >= 9)
                    yield return (year, month);
    }

    private static (int Year, int Month) PreviousQuarter(int year, int month) =>
        month == 3 ? (year - 1, 12) : (year, month - 3);

    private static char QuarterCode(int month) => month switch {
        3 => 'H', 6 => 'M', 9 => 'U', 12 => 'Z',
        _ => throw new ArgumentOutOfRangeException(nameof(month))
    };

    private static DateTimeOffset RollDateUtc((int Year, int Month) quarter) =>
        RollDateUtc(quarter.Year, quarter.Month);

    private static DateTimeOffset RollDateUtc(int year, int month)
    {
        // ES normally rolls on the Thursday eight calendar days before
        // the contract month's third Friday (quarterly March cycle).
        var first = new DateTime(year, month, 1);
        int offset = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        var thirdFriday = first.AddDays(offset + 14);
        var rollDay = DateOnly.FromDateTime(thirdFriday.AddDays(-8));
        return MarketTime.ToUtc(rollDay, new TimeOnly(9, 30));
    }

    public void Dispose() => _http.Dispose();
}
