using System.Data;
using System.Diagnostics;
using Prometheus;

namespace TransferService;

public enum LockMode
{
    Ordered,
    Naive,
    None
}

public sealed record TransferSettings(
    string DatabaseUrl,
    string JwtSecret,
    string JwtIssuer,
    string OtlpEndpoint,
    string RabbitHost,
    string RabbitVhost,
    string RabbitUser,
    string RabbitPassword,
    IsolationLevel DefaultIsolation,
    LockMode DefaultLockMode,
    int DefaultLockHoldMs,
    int MaxRetries)
{
    public static TransferSettings FromEnvironment() => new(
        Required("DATABASE_URL"),
        Required("JWT_SECRET"),
        Env("JWT_ISSUER", "bank-auth"),
        Env("OTLP_ENDPOINT", "http://tempo:4318/v1/traces"),
        Env("RABBIT_HOST", "rabbitmq"),
        Env("RABBIT_VHOST", "bank"),
        Env("RABBIT_USER", "transfer_service"),
        Env("RABBIT_PASS", "transfer_pass"),
        TransferOptions.ParseIsolation(Env("ISOLATION_LEVEL", "read-committed")) ?? IsolationLevel.ReadCommitted,
        TransferOptions.ParseLockMode(Env("LOCK_MODE", "ordered")) ?? LockMode.Ordered,
        int.Parse(Env("LOCK_HOLD_MS", "0")),
        int.Parse(Env("MAX_RETRIES", "0")));

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");
}

public sealed record TransferOptions(IsolationLevel Isolation, LockMode LockMode, int LockHoldMs, int MaxRetries, string? Error)
{
    public const int MaxLockHoldMs = 3000;

    public string IsolationName => IsolationLabel(Isolation);
    public string LockModeName => LockMode.ToString().ToLowerInvariant();

    public static TransferOptions Resolve(TransferSettings s, IHeaderDictionary headers)
    {
        var isolation = s.DefaultIsolation;
        var lockMode = s.DefaultLockMode;
        var hold = s.DefaultLockHoldMs;
        var retries = s.MaxRetries;

        if (headers.TryGetValue("X-Isolation-Level", out var iso) && !string.IsNullOrWhiteSpace(iso))
        {
            if (ParseIsolation(iso!) is not { } parsed)
                return new(isolation, lockMode, hold, retries, "X-Isolation-Level must be read-committed, repeatable-read or serializable");
            isolation = parsed;
        }

        if (headers.TryGetValue("X-Lock-Mode", out var lm) && !string.IsNullOrWhiteSpace(lm))
        {
            if (ParseLockMode(lm!) is not { } parsed)
                return new(isolation, lockMode, hold, retries, "X-Lock-Mode must be ordered, naive or none");
            lockMode = parsed;
        }

        if (headers.TryGetValue("X-Lock-Hold-Ms", out var h) && !string.IsNullOrWhiteSpace(h))
        {
            if (!int.TryParse(h, out var parsed) || parsed < 0 || parsed > MaxLockHoldMs)
                return new(isolation, lockMode, hold, retries, $"X-Lock-Hold-Ms must be 0-{MaxLockHoldMs}");
            hold = parsed;
        }

        if (headers.TryGetValue("X-Max-Retries", out var r) && !string.IsNullOrWhiteSpace(r))
        {
            if (!int.TryParse(r, out var parsed) || parsed < 0 || parsed > 5)
                return new(isolation, lockMode, hold, retries, "X-Max-Retries must be 0-5");
            retries = parsed;
        }

        return new(isolation, lockMode, hold, retries, null);
    }

    public static IsolationLevel? ParseIsolation(string value) => value.Trim().ToLowerInvariant() switch
    {
        "read-committed" or "readcommitted" or "read_committed" => IsolationLevel.ReadCommitted,
        "repeatable-read" or "repeatableread" or "repeatable_read" => IsolationLevel.RepeatableRead,
        "serializable" => IsolationLevel.Serializable,
        _ => null
    };

    public static LockMode? ParseLockMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ordered" => LockMode.Ordered,
        "naive" => LockMode.Naive,
        "none" => LockMode.None,
        _ => null
    };

    public static string IsolationLabel(IsolationLevel level) => level switch
    {
        IsolationLevel.ReadCommitted => "read-committed",
        IsolationLevel.RepeatableRead => "repeatable-read",
        IsolationLevel.Serializable => "serializable",
        _ => level.ToString().ToLowerInvariant()
    };
}

public static class Telemetry
{
    public const string SourceName = "transfer-service";
    public static readonly ActivitySource Source = new(SourceName);
}

public static class AppMetrics
{
    public static readonly Counter Transfers = Metrics.CreateCounter(
        "bank_transfers_total", "Transfer requests by final status", "status");

    public static readonly Counter ValidationErrors = Metrics.CreateCounter(
        "bank_transfer_validation_errors_total", "Rejected transfers by validation reason", "reason");

    public static readonly Histogram TransactionDuration = Metrics.CreateHistogram(
        "bank_transfer_tx_duration_seconds", "Duration of a single database transaction attempt",
        new HistogramConfiguration
        {
            LabelNames = new[] { "isolation", "lock_mode", "outcome" },
            Buckets = new[] { 0.002, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 1.5, 2, 3, 5 }
        });

    public static readonly Counter Conflicts = Metrics.CreateCounter(
        "bank_transfer_conflicts_total", "Database conflicts during transfers", "type", "isolation", "lock_mode");

    public static readonly Counter Retries = Metrics.CreateCounter(
        "bank_transfer_retries_total", "Transfer transactions retried after a conflict", "type");

    public static readonly Histogram Amount = Metrics.CreateHistogram(
        "bank_transfer_amount", "Amount of completed transfers",
        new HistogramConfiguration
        {
            LabelNames = new[] { "currency" },
            Buckets = new double[] { 100, 500, 1000, 5000, 10000, 25000, 50000, 100000, 250000 }
        });

    public static readonly Counter EventsPublished = Metrics.CreateCounter(
        "bank_events_published_total", "TransferInitiated events sent to RabbitMQ", "result");

    public static readonly Counter AccessDenied = Metrics.CreateCounter(
        "bank_api_access_denied_total", "Requests rejected by authentication or authorization", "reason");

    public static readonly Counter AccountsCreated = Metrics.CreateCounter(
        "bank_accounts_created_total", "Accounts opened through the API");

    public static readonly Gauge TotalBalance = Metrics.CreateGauge(
        "bank_total_balance", "Sum of all account balances", "currency");

    public static readonly Gauge BlockedAccounts = Metrics.CreateGauge(
        "bank_accounts_blocked", "Accounts blocked by the anti-fraud worker");

    public static readonly Gauge PendingFraudChecks = Metrics.CreateGauge(
        "bank_transfers_pending_fraud_check", "Completed transfers not yet analyzed by the anti-fraud worker");

    public static void Initialize()
    {
        foreach (var s in new[] { "completed", "rejected", "conflict", "error" }) Transfers.WithLabels(s);
        foreach (var r in new[] { "invalid_request", "invalid_amount", "same_account", "account_not_found", "account_blocked", "insufficient_funds", "currency_mismatch" })
            ValidationErrors.WithLabels(r);
        foreach (var r in new[] { "ok", "failed" }) EventsPublished.WithLabels(r);
        foreach (var r in new[] { "unauthorized", "forbidden" }) AccessDenied.WithLabels(r);
        foreach (var t in new[] { "deadlock", "serialization" }) Retries.WithLabels(t);
    }
}
