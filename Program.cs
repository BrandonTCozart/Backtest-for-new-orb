using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Configuration;

// Visual Studio: right-click this project -> Manage User Secrets once.
// Thereafter the green Start button downloads a fresh 3-month CSV on every run.
var secrets = new ConfigurationBuilder()
    .AddUserSecrets("dotnet-Backtest_for_new_orb-91f535b9-437c-4a70-ae84-ee85729d8eb9")
    .Build();
var user = Environment.GetEnvironmentVariable("TOPSTEP_USERNAME") ?? secrets["Topstep:UserName"];
var key = Environment.GetEnvironmentVariable("TOPSTEP_API_KEY") ?? secrets["Topstep:ApiKey"];
if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Topstep credentials are missing.");
    Console.Error.WriteLine("In Visual Studio: right-click Backtest for new orb -> Manage User Secrets.");
    Console.Error.WriteLine("Add JSON with Topstep:UserName and Topstep:ApiKey, save, and press Start again.");
    Console.Error.WriteLine("Do not put API keys in GitHub or launchSettings.json.");
    return 1;
}
Console.WriteLine("Starting Topstep ES 1-minute historical download from Visual Studio.");
var from = DateTimeOffset.UtcNow.AddMonths(-3);
var to = DateTimeOffset.UtcNow;
// Always save beside the Visual Studio project, regardless of debugger working directory.
string FindProjectFolder()
{
    DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
    while (folder != null)
    {
        if (File.Exists(Path.Combine(folder.FullName, "Backtest for new orb.csproj")))
            return folder.FullName;
        folder = folder.Parent;
    }
    return Directory.GetCurrentDirectory();
}
var output = Path.Combine(FindProjectFolder(), "data", "es_1m_3months.csv");
Console.WriteLine($"UTC interval: {from:O} to {to:O}");
var allowPartial = args.Contains("--allow-partial");
var ids = new Dictionary<string,string>();
for (int i = 0; i < args.Length; ++i)
{
    if (args[i] == "--start") from = DateTimeOffset.Parse(args[++i]).ToUniversalTime();
    if (args[i] == "--end") to = DateTimeOffset.Parse(args[++i]).ToUniversalTime();
    if (args[i] == "--output") output = args[++i];
    if (args[i] == "--contract") { var p = args[++i].Split('=', 2); ids[p[0]] = p[1]; }
}
if (from >= to) throw new ArgumentException("Start must precede end.");
using var client = new HttpClient { BaseAddress = new Uri("https://api.topstepx.com"), Timeout = TimeSpan.FromSeconds(60) };
string? token = null;
DateTimeOffset next = DateTimeOffset.MinValue;
async Task<JsonElement> Post(string path, object payload)
{
    for (int attempt = 0; attempt < 6; attempt++)
    {
        var delay = next - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        next = DateTimeOffset.UtcNow.AddMilliseconds(350);
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(req);
        if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < 5)
        {
            await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            continue;
        }
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (data.TryGetProperty("success", out var success) && !success.GetBoolean())
            throw new Exception(path + " " + (data.TryGetProperty("errorMessage", out var error) ? error.ToString() : "API rejected request"));
        return data;
    }
    throw new Exception("Retries exhausted");
}
token = (await Post("/api/Auth/loginKey", new { userName = user, apiKey = key })).GetProperty("token").GetString();
var results = new SortedDictionary<DateTimeOffset, string>();
var problems = new List<string>();
// ES contract rollover approximation: 8 days before the third Friday in Mar, Jun, Sep, Dec.
var rolls = new List<(DateTimeOffset date, string symbol)>();
for (var y = from.Year - 1; y <= to.Year + 1; y++)
    foreach (var (m, code) in new[] { (3,"H"), (6,"M"), (9,"U"), (12,"Z") })
    {
        var first = new DateTime(y, m, 1);
        var thirdFriday = first.AddDays(((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7 + 14);
        rolls.Add((new DateTimeOffset(thirdFriday.AddDays(-8), TimeSpan.Zero), "ES" + code + (y % 10)));
    }
rolls.Sort((a,b) => a.date.CompareTo(b.date));
for (var cursor = from; cursor < to;)
{
    var roll = rolls.First(r => r.date > cursor);
    var segmentEnd = roll.date < to ? roll.date : to;
    Console.WriteLine("Contract " + roll.symbol + " starting " + cursor.ToString("O"));
    string id;
    try
    {
        if (!ids.TryGetValue(roll.symbol, out id))
        {
            // /Contract/search returns a limited list and may omit expired contracts.
            // ProjectX documents an ID lookup that can return inactive quarterly contracts.
            var expectedId = $"CON.F.US.EP.{roll.symbol[2]}{roll.date.Year % 100:00}";
            Console.WriteLine($"Resolving {roll.symbol} by ID: {expectedId}");
            string? resolvedId = null;
            try
            {
                var details = await Post("/api/Contract/searchById", new { contractId = expectedId });
                if (details.TryGetProperty("contract", out var contract) &&
                    contract.ValueKind == JsonValueKind.Object &&
                    contract.TryGetProperty("id", out var contractId) &&
                    string.Equals(contractId.GetString(), expectedId, StringComparison.OrdinalIgnoreCase) &&
                    contract.TryGetProperty("symbolId", out var symbolId) &&
                    symbolId.GetString() == "F.US.EP" &&
                    contract.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), roll.symbol, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedId = expectedId;
                }
                else
                {
                    Console.WriteLine($"ID lookup did not return a matching ES contract for {roll.symbol}.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ID lookup unavailable for {roll.symbol}: {ex.Message}. Trying name search.");
            }

            if (resolvedId is null)
            {
                var response = await Post("/api/Contract/search", new { searchText = roll.symbol, live = false });
                var matches = response.GetProperty("contracts").EnumerateArray().Where(c =>
                    c.TryGetProperty("name", out var name) &&
                    string.Equals(name.GetString(), roll.symbol, StringComparison.OrdinalIgnoreCase) &&
                    c.TryGetProperty("symbolId", out var symbolId) &&
                    symbolId.GetString() == "F.US.EP").ToArray();
                if (matches.Length != 1)
                    throw new Exception($"Could not identify expired contract {roll.symbol} through lookup-by-ID or search. Topstep may not expose it.");
                resolvedId = matches[0].GetProperty("id").GetString();
            }
            id = resolvedId ?? throw new Exception("Contract lookup returned no ID.");
        }
        Console.WriteLine($"Using contract ID for {roll.symbol}: {id}");
    }
    catch (Exception ex) { problems.Add(roll.symbol + ": " + ex.Message); cursor = segmentEnd; continue; }
    for (var start = cursor; start < segmentEnd;)
    {
        var end = start.AddDays(7) < segmentEnd ? start.AddDays(7) : segmentEnd;
        try
        {
            var response = await Post("/api/History/retrieveBars", new {
                contractId = id, live = false, startTime = start, endTime = end,
                unit = 2, unitNumber = 1, limit = 20000, includePartialBar = false
            });
            var bars = response.GetProperty("bars").EnumerateArray().ToArray();
            if (bars.Length == 0) problems.Add("Empty window " + roll.symbol + " " + start.ToString("O"));
            foreach (var bar in bars)
            {
                var stamp = bar.GetProperty("t").GetDateTimeOffset().ToUniversalTime();
                if (stamp < start || stamp >= end) continue;
                string D(string name) => bar.GetProperty(name).GetDecimal().ToString(CultureInfo.InvariantCulture);
                results[stamp] = string.Join(",",stamp.ToString("yyyy-MM-ddTHH:mm:ssZ"), roll.symbol,
                    D("o"), D("h"), D("l"), D("c"), bar.GetProperty("v").GetInt64().ToString(CultureInfo.InvariantCulture));
            }
            Console.WriteLine(roll.symbol + " " + start.ToString("yyyy-MM-dd") + ": " + bars.Length + " bars");
        }
        catch (Exception ex) { problems.Add(roll.symbol + " " + start.ToString("O") + ": " + ex.Message); }
        start = end;
    }
    cursor = segmentEnd;
}
foreach (var problem in problems) Console.Error.WriteLine("WARNING " + problem);
if (results.Count == 0 || (problems.Count != 0 && !allowPartial))
{
    Console.Error.WriteLine("No complete CSV generated. Use --allow-partial only after checking missing data.");
    return 1;
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllLinesAsync(output, new[] { "timestamp_utc,contract,open,high,low,close,volume" }.Concat(results.Values));
Console.WriteLine("Saved " + results.Count + " bars to " + output);
return problems.Count == 0 ? 0 : 2;
