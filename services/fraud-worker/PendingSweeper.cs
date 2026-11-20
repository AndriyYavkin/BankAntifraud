using System.Diagnostics;
using Npgsql;
using Prometheus;

namespace FraudWorker;

public sealed class PendingSweeper(NpgsqlDataSource db, FraudAnalyzer analyzer, WorkerSettings s, ILogger<PendingSweeper> log) : BackgroundService
{
    private static readonly Counter Recovered = Metrics.CreateCounter(
        "bank_fraud_recovered_total", "Pending transfers analyzed by the sweeper because their event was not received");

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(s.SweepIntervalSeconds));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Pending transfer sweep failed: {Error}", ex.Message);
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var pending = new List<TransferInitiated>();
        await using (var cmd = db.CreateCommand(
            """
            SELECT id, from_account_id, to_account_id, amount, currency, from_balance_after, initiated_by, created_at
            FROM transfers
            WHERE fraud_status = 'pending' AND created_at < now() - make_interval(secs => @age)
            ORDER BY created_at
            LIMIT 100
            """))
        {
            cmd.Parameters.AddWithValue("age", (double)s.SweepAgeSeconds);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                pending.Add(new TransferInitiated("TransferInitiated", r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetDecimal(3),
                    r.GetString(4), r.GetDecimal(5), r.GetString(6), r.GetDateTime(7)));
        }

        if (pending.Count == 0) return;
        log.LogWarning("Found {Count} transfers without fraud check older than {Age} s, analyzing them directly", pending.Count, s.SweepAgeSeconds);

        foreach (var evt in pending)
        {
            using var activity = WorkerTelemetry.Source.StartActivity("pending transfer recovery");
            activity?.SetTag("transfer.id", evt.TransferId.ToString());
            var result = await analyzer.AnalyzeAsync(evt, ct);
            if (result.Status is AnalysisStatus.Analyzed or AnalysisStatus.Whitelisted)
            {
                Recovered.Inc();
                WorkerMetrics.Checks.WithLabels(result.Status == AnalysisStatus.Whitelisted ? "whitelisted" : result.Suspicious ? "suspicious" : "clean").Inc();
                foreach (var hit in result.Hits) WorkerMetrics.RuleHits.WithLabels(hit.Rule).Inc();
                if (result.AccountBlocked) WorkerMetrics.AccountsBlocked.Inc();
                log.LogInformation("Recovered transfer {TransferId}: score {FraudScore}, suspicious {Suspicious}, account {AccountId} blocked {Blocked}",
                    evt.TransferId, result.Score, result.Suspicious, evt.FromAccountId, result.AccountBlocked);
            }
        }
    }
}
