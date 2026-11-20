using System.Diagnostics;
using System.Globalization;
using Prometheus;

namespace FraudWorker;

public sealed record WorkerSettings(
    string DatabaseUrl,
    string OtlpEndpoint,
    string RabbitHost,
    string RabbitVhost,
    string RabbitUser,
    string RabbitPassword,
    string Queue,
    ushort Prefetch,
    int AnalysisDelayMs,
    decimal HighAmountThreshold,
    int VelocityWindowSeconds,
    int VelocityMaxTransfers,
    int PingPongWindowSeconds,
    int FanOutWindowSeconds,
    int FanOutMaxRecipients,
    decimal DrainRatio,
    decimal DrainMinAmount,
    int SuspiciousScore,
    int BlockScore,
    int SweepIntervalSeconds,
    int SweepAgeSeconds)
{
    public static WorkerSettings FromEnvironment() => new(
        Environment.GetEnvironmentVariable("DATABASE_URL") ?? throw new InvalidOperationException("DATABASE_URL is not set"),
        Env("OTLP_ENDPOINT", "http://tempo:4318/v1/traces"),
        Env("RABBIT_HOST", "rabbitmq"),
        Env("RABBIT_VHOST", "bank"),
        Env("RABBIT_USER", "fraud_worker"),
        Env("RABBIT_PASS", "fraud_pass"),
        Env("RABBIT_QUEUE", "transfers.fraud-check"),
        ushort.Parse(Env("PREFETCH", "10")),
        int.Parse(Env("ANALYSIS_DELAY_MS", "0")),
        Dec("HIGH_AMOUNT_THRESHOLD", "50000"),
        int.Parse(Env("VELOCITY_WINDOW_SECONDS", "60")),
        int.Parse(Env("VELOCITY_MAX_TRANSFERS", "5")),
        int.Parse(Env("PING_PONG_WINDOW_SECONDS", "300")),
        int.Parse(Env("FAN_OUT_WINDOW_SECONDS", "300")),
        int.Parse(Env("FAN_OUT_MAX_RECIPIENTS", "4")),
        Dec("DRAIN_RATIO", "0.9"),
        Dec("DRAIN_MIN_AMOUNT", "1000"),
        int.Parse(Env("SUSPICIOUS_SCORE", "50")),
        int.Parse(Env("BLOCK_SCORE", "80")),
        int.Parse(Env("SWEEP_INTERVAL_SECONDS", "15")),
        int.Parse(Env("SWEEP_AGE_SECONDS", "60")));

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

    private static decimal Dec(string name, string fallback) => decimal.Parse(Env(name, fallback), CultureInfo.InvariantCulture);
}

public static class WorkerTelemetry
{
    public const string SourceName = "fraud-worker";
    public static readonly ActivitySource Source = new(SourceName);
}

public static class WorkerMetrics
{
    public static readonly string[] Rules = { "high_amount", "velocity", "ping_pong", "fan_out", "balance_drain", "round_amount" };

    public static readonly Counter Checks = Metrics.CreateCounter(
        "bank_fraud_checks_total", "Processed TransferInitiated events by result", "result");

    public static readonly Counter RuleHits = Metrics.CreateCounter(
        "bank_fraud_rule_hits_total", "Anti-fraud rules triggered", "rule");

    public static readonly Counter AccountsBlocked = Metrics.CreateCounter(
        "bank_fraud_accounts_blocked_total", "Accounts blocked automatically by the anti-fraud worker");

    public static readonly Histogram Score = Metrics.CreateHistogram(
        "bank_fraud_score", "Fraud score assigned to transfers",
        new HistogramConfiguration { Buckets = new double[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 } });

    public static readonly Histogram AnalysisDuration = Metrics.CreateHistogram(
        "bank_fraud_analysis_duration_seconds", "Time spent analyzing one transfer",
        new HistogramConfiguration { Buckets = new[] { 0.002, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5 } });

    public static readonly Histogram EventLag = Metrics.CreateHistogram(
        "bank_fraud_event_lag_seconds", "Delay between transfer commit and fraud analysis",
        new HistogramConfiguration { Buckets = new[] { 0.01, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300 } });

    public static readonly Gauge ConsumerConnected = Metrics.CreateGauge(
        "bank_fraud_consumer_connected", "1 when the worker is consuming from RabbitMQ");

    public static void Initialize()
    {
        foreach (var r in new[] { "clean", "suspicious", "whitelisted", "duplicate", "invalid", "error" }) Checks.WithLabels(r);
        foreach (var r in Rules) RuleHits.WithLabels(r);
    }
}
