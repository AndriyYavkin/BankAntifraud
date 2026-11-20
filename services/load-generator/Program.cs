using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "help";
var options = Options.Parse(args);

if (command is "help" or "-h")
{
    Console.WriteLine("""
        Usage: load-generator <scenario> [options]

        Scenarios:
          normal        random small transfers between active UAH accounts
          fraud         suspicious patterns: velocity burst, fan-out, balance drain, ping-pong
          deadlock      concurrent opposite transfers A->B and B->A between settlement accounts
          lost-update   concurrent transfers from one settlement account without row locks
          invalid       balance and validation errors
          unauthorized  requests without token, with a forged token and with a wrong role
          seed          create new accounts
          mixed         normal traffic with periodic fraud and deadlock bursts

        Options:
          --count N         number of transfers or pairs (default 20)
          --parallel N      concurrent requests (default 2)
          --delay-ms N      pause between requests (default 500)
          --duration S      seconds for the mixed scenario (default 120)
          --isolation X     read-committed | repeatable-read | serializable
          --lock-mode X     ordered | naive | none
          --hold-ms N       pause between locking the two accounts (default 0, deadlock 300)
          --retries N       retries after deadlock or serialization failure (0-5)
          --user NAME       login (default operator)
          --password PASS   password (default operator_pass)
        """);
    return 0;
}

var authUrl = Environment.GetEnvironmentVariable("AUTH_URL") ?? "http://localhost:8002";
var apiUrl = Environment.GetEnvironmentVariable("API_URL") ?? "http://localhost:8000";
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var stats = new Stats();

var token = await LoginAsync(options.User, options.Password);
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
Console.WriteLine($"Logged in as {options.User}. API: {apiUrl}");

var sw = Stopwatch.StartNew();
switch (command)
{
    case "normal": await NormalAsync(options.Count); break;
    case "fraud": await FraudAsync(); break;
    case "deadlock": await DeadlockAsync(); break;
    case "lost-update": await LostUpdateAsync(); break;
    case "invalid": await InvalidAsync(); break;
    case "unauthorized": await UnauthorizedAsync(); break;
    case "seed": await SeedAsync(); break;
    case "mixed": await MixedAsync(); break;
    default:
        Console.Error.WriteLine($"Unknown scenario '{command}'. Run with 'help'.");
        return 1;
}

stats.Print(sw.Elapsed);
return 0;

async Task<string> LoginAsync(string user, string password)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            var resp = await http.PostAsJsonAsync($"{authUrl}/login", new { username = user, password }, json);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"login failed: {(int)resp.StatusCode} {body}");
            return body.GetProperty("access_token").GetString()!;
        }
        catch (HttpRequestException) when (attempt < 10)
        {
            Console.WriteLine($"auth-service is not reachable yet, retry {attempt}/10");
            await Task.Delay(3000);
        }
    }
}

async Task<List<Account>> AccountsAsync(string currency = "UAH", bool activeOnly = true, bool? internalOnly = false)
{
    var all = await http.GetFromJsonAsync<List<Account>>($"{apiUrl}/api/accounts?limit=500", json) ?? new();
    return all.Where(a => a.Currency == currency && (!activeOnly || !a.IsBlocked) && (internalOnly is null || a.IsInternal == internalOnly)).ToList();
}

async Task<HttpStatusCode> TransferAsync(int from, int to, decimal amount, string? reference = null, string? isolation = null, string? lockMode = null, int? holdMs = null, int? retries = null)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/transfers")
    {
        Content = JsonContent.Create(new { from_account_id = from, to_account_id = to, amount, reference }, options: json)
    };
    if ((isolation ?? options.Isolation) is { } iso) req.Headers.Add("X-Isolation-Level", iso);
    if ((lockMode ?? options.LockMode) is { } lm) req.Headers.Add("X-Lock-Mode", lm);
    if ((holdMs ?? options.HoldMs) is { } h) req.Headers.Add("X-Lock-Hold-Ms", h.ToString());
    if ((retries ?? options.Retries) is { } r) req.Headers.Add("X-Max-Retries", r.ToString());

    var t = Stopwatch.StartNew();
    try
    {
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var error = resp.IsSuccessStatusCode ? null : TryError(body);
        stats.Add((int)resp.StatusCode, error, t.Elapsed);
        var label = resp.IsSuccessStatusCode ? "ok" : error;
        Console.WriteLine($"  {from,4} -> {to,-4} {amount.ToString("0.00", CultureInfo.InvariantCulture),12}  {(int)resp.StatusCode} {label,-22} {t.ElapsedMilliseconds,5} ms");
        return resp.StatusCode;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        stats.Add(0, "network_error", t.Elapsed);
        Console.WriteLine($"  {from,4} -> {to,-4} network error: {ex.Message}");
        return 0;
    }
}

static string? TryError(string body)
{
    try
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : body;
    }
    catch (JsonException)
    {
        return string.IsNullOrWhiteSpace(body) ? "empty" : body;
    }
}

async Task RunParallelAsync(int count, int parallel, Func<int, Task> work)
{
    await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (i, _) =>
    {
        await work(i);
        if (options.DelayMs > 0) await Task.Delay(options.DelayMs);
    });
}

decimal RandomAmount(int min, int max) => Math.Round((decimal)(Random.Shared.Next(min * 100, max * 100) / 100.0), 2);

async Task NormalAsync(int count)
{
    var accounts = await AccountsAsync();
    if (accounts.Count < 2) throw new InvalidOperationException("need at least two active UAH accounts");
    Console.WriteLine($"Normal traffic: {count} transfers between {accounts.Count} accounts");
    await RunParallelAsync(count, options.Parallel, async _ =>
    {
        var a = accounts[Random.Shared.Next(accounts.Count)];
        Account b;
        do b = accounts[Random.Shared.Next(accounts.Count)]; while (b.Id == a.Id);
        await TransferAsync(a.Id, b.Id, RandomAmount(50, 3000), "payment");
    });
}

async Task FraudAsync()
{
    var accounts = await AccountsAsync();
    var rich = accounts.Where(a => a.Balance > 20_000).OrderBy(_ => Random.Shared.Next()).ToList();
    if (rich.Count < 2 || accounts.Count < 8) throw new InvalidOperationException("need active accounts with balance above 20000; run 'seed' first");

    var mule = rich[0];
    var others = accounts.Where(a => a.Id != mule.Id).OrderBy(_ => Random.Shared.Next()).Take(6).ToList();

    Console.WriteLine($"1) Velocity and fan-out burst from account {mule.Id}");
    foreach (var target in others.Take(6))
        await TransferAsync(mule.Id, target.Id, RandomAmount(200, 900), "burst");

    Console.WriteLine($"2) Ping-pong loop between {rich[1].Id} and {others[0].Id}");
    for (var i = 0; i < 2; i++)
    {
        await TransferAsync(rich[1].Id, others[0].Id, 5000 - i * 10, "loop");
        await TransferAsync(others[0].Id, rich[1].Id, 4990 - i * 10, "loop");
    }

    var fresh = (await AccountsAsync()).FirstOrDefault(a => a.Id == mule.Id);
    if (fresh is { IsBlocked: false })
    {
        var drain = Math.Floor(fresh.Balance * 0.95m / 1000) * 1000;
        Console.WriteLine($"3) Round high-value balance drain from account {mule.Id}");
        if (drain > 0) await TransferAsync(mule.Id, others[1].Id, drain, "urgent");
    }

    await Task.Delay(1500);
    Console.WriteLine($"4) Next transfer from account {mule.Id} (should be rejected if the account was blocked)");
    await TransferAsync(mule.Id, others[2].Id, 100, "after-block");
}

async Task DeadlockAsync()
{
    var accounts = await AccountsAsync(internalOnly: true);
    var pairs = accounts.Where(a => a.Balance > 1000).OrderBy(_ => Random.Shared.Next()).Take(4).ToList();
    if (pairs.Count < 2) throw new InvalidOperationException("need at least two internal settlement accounts with balance above 1000");
    var lockMode = options.LockMode ?? "naive";
    var hold = options.HoldMs ?? 300;
    Console.WriteLine($"Opposite transfers: {options.Count} pairs, lock mode {lockMode}, isolation {options.Isolation ?? "default"}, hold {hold} ms");

    await RunParallelAsync(options.Count, Math.Max(1, options.Parallel / 2), async i =>
    {
        var a = pairs[i % pairs.Count];
        var b = pairs[(i + 1) % pairs.Count];
        await Task.WhenAll(
            TransferAsync(a.Id, b.Id, RandomAmount(10, 100), "counter-a", lockMode: lockMode, holdMs: hold),
            TransferAsync(b.Id, a.Id, RandomAmount(10, 100), "counter-b", lockMode: lockMode, holdMs: hold));
    });
}

async Task LostUpdateAsync()
{
    var accounts = await AccountsAsync(internalOnly: true);
    var source = accounts.Where(a => a.Balance > 5000).OrderBy(_ => Random.Shared.Next()).First();
    var targets = accounts.Where(a => a.Id != source.Id).Take(5).ToList();
    var lockMode = options.LockMode ?? "none";
    var hold = options.HoldMs ?? 150;

    var before = await TotalAsync();
    Console.WriteLine($"Concurrent transfers from account {source.Id} (balance {source.Balance}), lock mode {lockMode}, isolation {options.Isolation ?? "default"}");
    Console.WriteLine($"Total UAH before: {before:0.00}");

    await RunParallelAsync(options.Count, Math.Max(options.Parallel, 4), async i =>
        await TransferAsync(source.Id, targets[i % targets.Count].Id, 100, "race", lockMode: lockMode, holdMs: hold));

    var after = await TotalAsync();
    Console.WriteLine($"Total UAH after:  {after:0.00}");
    Console.WriteLine(after == before
        ? "Consistent: the total amount of money did not change."
        : $"INCONSISTENT: {after - before:+0.00;-0.00} UAH appeared from lost updates.");
}

async Task<decimal> TotalAsync() => (await AccountsAsync("UAH", activeOnly: false, internalOnly: null)).Sum(a => a.Balance);

async Task InvalidAsync()
{
    var accounts = await AccountsAsync();
    var usd = (await AccountsAsync("USD")).FirstOrDefault();
    var poor = accounts.OrderBy(a => a.Balance).First();
    var other = accounts.First(a => a.Id != poor.Id);

    for (var i = 0; i < Math.Max(1, options.Count / 5); i++)
    {
        Console.WriteLine("Insufficient funds");
        await TransferAsync(poor.Id, other.Id, poor.Balance + 1000);
        Console.WriteLine("Account not found");
        await TransferAsync(other.Id, 999_999, 10);
        Console.WriteLine("Same account");
        await TransferAsync(other.Id, other.Id, 10);
        Console.WriteLine("Invalid amount");
        await TransferAsync(other.Id, poor.Id, -5);
        if (usd is not null)
        {
            Console.WriteLine("Currency mismatch");
            await TransferAsync(other.Id, usd.Id, 10);
        }
        if (options.DelayMs > 0) await Task.Delay(options.DelayMs);
    }
}

async Task UnauthorizedAsync()
{
    HttpContent Body() => JsonContent.Create(new { from_account_id = 1, to_account_id = 2, amount = 10 }, options: json);
    using var anon = new HttpClient();

    for (var i = 0; i < Math.Max(1, options.Count / 4); i++)
    {
        var r1 = await anon.PostAsync($"{apiUrl}/api/transfers", Body());
        stats.Add((int)r1.StatusCode, "no_token", TimeSpan.Zero);
        Console.WriteLine($"  without token        -> {(int)r1.StatusCode}");

        using var forged = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/transfers") { Content = Body() };
        forged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJoYWNrZXIiLCJyb2xlIjoiYWRtaW4ifQ.c2lnbmF0dXJl");
        var r2 = await anon.SendAsync(forged);
        stats.Add((int)r2.StatusCode, "forged_token", TimeSpan.Zero);
        Console.WriteLine($"  forged token         -> {(int)r2.StatusCode}");

        var r3 = await anon.PostAsJsonAsync($"{authUrl}/login", new { username = "operator", password = "wrong" });
        stats.Add((int)r3.StatusCode, "wrong_password", TimeSpan.Zero);
        Console.WriteLine($"  wrong password       -> {(int)r3.StatusCode}");

        var analyst = await LoginAsync("analyst", "analyst_pass");
        using var asAnalyst = new HttpRequestMessage(HttpMethod.Post, $"{apiUrl}/api/transfers") { Content = Body() };
        asAnalyst.Headers.Authorization = new AuthenticationHeaderValue("Bearer", analyst);
        var r4 = await anon.SendAsync(asAnalyst);
        stats.Add((int)r4.StatusCode, "wrong_role", TimeSpan.Zero);
        Console.WriteLine($"  analyst makes transfer -> {(int)r4.StatusCode}");
        if (options.DelayMs > 0) await Task.Delay(options.DelayMs);
    }
}

async Task SeedAsync()
{
    var names = new[] { "Olena Kovalenko", "Taras Shevchuk", "Iryna Bondar", "Andrii Melnyk", "Oksana Tkachenko", "Dmytro Kravets", "Natalia Moroz", "Serhii Lysenko", "Yulia Savchenko", "Maksym Rudenko" };
    for (var i = 0; i < options.Count; i++)
    {
        var name = names[Random.Shared.Next(names.Length)];
        var balance = RandomAmount(5_000, 150_000);
        var resp = await http.PostAsJsonAsync($"{apiUrl}/api/accounts", new { owner_name = name, initial_balance = balance, currency = "UAH" }, json);
        var created = await resp.Content.ReadFromJsonAsync<Account>(json);
        stats.Add((int)resp.StatusCode, resp.IsSuccessStatusCode ? null : "seed_failed", TimeSpan.Zero);
        Console.WriteLine($"  {(int)resp.StatusCode} account {created?.Id} {name} {balance:0.00} UAH");
    }
}

async Task MixedAsync()
{
    var until = DateTime.UtcNow.AddSeconds(options.Duration);
    var round = 0;
    while (DateTime.UtcNow < until)
    {
        round++;
        Console.WriteLine($"--- round {round}, {(int)(until - DateTime.UtcNow).TotalSeconds}s left");
        await NormalAsync(10);
        await Task.Delay(2000);
        if (round % 3 == 0) await DeadlockAsync();
        if (round % 4 == 0) await InvalidAsync();
        if (round % 5 == 0) await FraudAsync();
    }
}

sealed record Account(int Id, string OwnerName, decimal Balance, string Currency, bool IsBlocked, bool IsInternal);

sealed class Stats
{
    private readonly ConcurrentDictionary<string, int> _counts = new();
    private readonly ConcurrentBag<double> _latencies = new();

    public void Add(int status, string? error, TimeSpan latency)
    {
        _counts.AddOrUpdate(error is null ? $"{status} ok" : $"{status} {error}", 1, (_, v) => v + 1);
        if (latency > TimeSpan.Zero) _latencies.Add(latency.TotalMilliseconds);
    }

    public void Print(TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine($"Summary ({elapsed.TotalSeconds:0.0} s):");
        foreach (var (key, value) in _counts.OrderBy(k => k.Key)) Console.WriteLine($"  {key,-30} {value}");
        if (_latencies.IsEmpty) return;
        var sorted = _latencies.OrderBy(x => x).ToArray();
        Console.WriteLine($"  latency p50 {sorted[sorted.Length / 2]:0} ms, p95 {sorted[(int)(sorted.Length * 0.95)]:0} ms, max {sorted[^1]:0} ms");
    }
}

sealed record Options(int Count, int Parallel, int DelayMs, int Duration, string? Isolation, string? LockMode, int? HoldMs, int? Retries, string User, string Password)
{
    public static Options Parse(string[] args)
    {
        string? Get(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        int? Int(string name) => Get(name) is { } v ? int.Parse(v) : null;

        return new Options(
            Int("--count") ?? 20,
            Int("--parallel") ?? 2,
            Int("--delay-ms") ?? 500,
            Int("--duration") ?? 120,
            Get("--isolation"),
            Get("--lock-mode"),
            Int("--hold-ms"),
            Int("--retries"),
            Get("--user") ?? Environment.GetEnvironmentVariable("LOADGEN_USER") ?? "operator",
            Get("--password") ?? Environment.GetEnvironmentVariable("LOADGEN_PASSWORD") ?? "operator_pass");
    }
}
