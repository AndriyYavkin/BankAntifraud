using System.Text.Json;
using Npgsql;

namespace FraudWorker;

public sealed record TransferInitiated(
    string EventType,
    Guid TransferId,
    int FromAccountId,
    int ToAccountId,
    decimal Amount,
    string Currency,
    decimal FromBalanceAfter,
    string InitiatedBy,
    DateTime CreatedAt)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

public sealed record RuleHit(string Rule, int Score, string Details);

public enum AnalysisStatus
{
    Analyzed,
    Duplicate,
    TransferMissing,
    Whitelisted
}

public sealed record AnalysisResult(AnalysisStatus Status, int Score, bool Suspicious, bool AccountBlocked, IReadOnlyList<RuleHit> Hits);

public sealed class FraudAnalyzer(NpgsqlDataSource db, WorkerSettings s)
{
    public async Task<AnalysisResult> AnalyzeAsync(TransferInitiated evt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        await using (var check = new NpgsqlCommand("SELECT fraud_status FROM transfers WHERE id = @id FOR UPDATE", conn, tx))
        {
            check.Parameters.AddWithValue("id", evt.TransferId);
            var status = (string?)await check.ExecuteScalarAsync(ct);
            if (status is null) return new AnalysisResult(AnalysisStatus.TransferMissing, 0, false, false, Array.Empty<RuleHit>());
            if (status != "pending") return new AnalysisResult(AnalysisStatus.Duplicate, 0, false, false, Array.Empty<RuleHit>());
        }

        await using (var internalCheck = new NpgsqlCommand(
            "SELECT count(*) = 2 AND bool_and(is_internal) FROM accounts WHERE id IN (@from, @to)", conn, tx))
        {
            internalCheck.Parameters.AddWithValue("from", evt.FromAccountId);
            internalCheck.Parameters.AddWithValue("to", evt.ToAccountId);
            if ((bool)(await internalCheck.ExecuteScalarAsync(ct))!)
            {
                await MarkAsync(conn, tx, evt.TransferId, "clean", 0, ct);
                await tx.CommitAsync(ct);
                return new AnalysisResult(AnalysisStatus.Whitelisted, 0, false, false, Array.Empty<RuleHit>());
            }
        }

        var hits = new List<RuleHit>();

        if (evt.Amount >= s.HighAmountThreshold)
            hits.Add(new RuleHit("high_amount", 40, $"amount {evt.Amount} {evt.Currency} >= threshold {s.HighAmountThreshold}"));

        if (evt.Amount >= 10_000 && evt.Amount % 1000 == 0)
            hits.Add(new RuleHit("round_amount", 10, $"round amount {evt.Amount} {evt.Currency}"));

        var balanceBefore = evt.FromBalanceAfter + evt.Amount;
        if (evt.Amount >= s.DrainMinAmount && balanceBefore > 0 && evt.Amount / balanceBefore >= s.DrainRatio)
            hits.Add(new RuleHit("balance_drain", 25, $"transfer took {evt.Amount / balanceBefore:P0} of balance {balanceBefore}"));

        var maxWindow = Math.Max(s.VelocityWindowSeconds, Math.Max(s.PingPongWindowSeconds, s.FanOutWindowSeconds));
        await using (var stats = new NpgsqlCommand(
            """
            SELECT
                count(*) FILTER (WHERE from_account_id = @from AND created_at > @t - make_interval(secs => @velocity)),
                count(DISTINCT to_account_id) FILTER (WHERE from_account_id = @from AND created_at > @t - make_interval(secs => @fanout)),
                count(*) FILTER (WHERE from_account_id = @to AND to_account_id = @from AND created_at > @t - make_interval(secs => @pingpong))
            FROM transfers
            WHERE from_account_id IN (@from, @to)
              AND created_at <= @t
              AND created_at > @t - make_interval(secs => @maxWindow)
            """, conn, tx))
        {
            stats.Parameters.AddWithValue("from", evt.FromAccountId);
            stats.Parameters.AddWithValue("to", evt.ToAccountId);
            stats.Parameters.AddWithValue("t", evt.CreatedAt);
            stats.Parameters.AddWithValue("velocity", (double)s.VelocityWindowSeconds);
            stats.Parameters.AddWithValue("fanout", (double)s.FanOutWindowSeconds);
            stats.Parameters.AddWithValue("pingpong", (double)s.PingPongWindowSeconds);
            stats.Parameters.AddWithValue("maxWindow", (double)maxWindow);

            await using var r = await stats.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var recent = r.GetInt64(0);
            var recipients = r.GetInt64(1);
            var reverse = r.GetInt64(2);

            if (recent >= s.VelocityMaxTransfers)
                hits.Add(new RuleHit("velocity", 35, $"{recent} transfers from account {evt.FromAccountId} in {s.VelocityWindowSeconds}s (limit {s.VelocityMaxTransfers})"));
            if (recipients >= s.FanOutMaxRecipients)
                hits.Add(new RuleHit("fan_out", 25, $"{recipients} distinct recipients from account {evt.FromAccountId} in {s.FanOutWindowSeconds}s (limit {s.FanOutMaxRecipients})"));
            if (reverse > 0)
                hits.Add(new RuleHit("ping_pong", (int)Math.Min(60, 30 * reverse), $"{reverse} reverse transfers {evt.ToAccountId} -> {evt.FromAccountId} in {s.PingPongWindowSeconds}s"));
        }

        var score = Math.Min(100, hits.Sum(h => h.Score));
        var suspicious = score >= s.SuspiciousScore;

        if (suspicious)
        {
            foreach (var hit in hits)
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO fraud_alerts (transfer_id, account_id, rule, score, details) VALUES (@transfer, @account, @rule, @score, @details)", conn, tx);
                insert.Parameters.AddWithValue("transfer", evt.TransferId);
                insert.Parameters.AddWithValue("account", evt.FromAccountId);
                insert.Parameters.AddWithValue("rule", hit.Rule);
                insert.Parameters.AddWithValue("score", hit.Score);
                insert.Parameters.AddWithValue("details", hit.Details);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await MarkAsync(conn, tx, evt.TransferId, suspicious ? "suspicious" : "clean", score, ct);

        var blocked = false;
        if (score >= s.BlockScore)
        {
            await using var block = new NpgsqlCommand(
                "UPDATE accounts SET is_blocked = true WHERE id = @id AND NOT is_blocked", conn, tx);
            block.Parameters.AddWithValue("id", evt.FromAccountId);
            blocked = await block.ExecuteNonQueryAsync(ct) == 1;
        }

        await tx.CommitAsync(ct);
        return new AnalysisResult(AnalysisStatus.Analyzed, score, suspicious, blocked, hits);
    }

    private static async Task MarkAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, string status, int score, CancellationToken ct)
    {
        await using var update = new NpgsqlCommand(
            "UPDATE transfers SET fraud_status = @status, fraud_score = @score, checked_at = now() WHERE id = @id", conn, tx);
        update.Parameters.AddWithValue("status", status);
        update.Parameters.AddWithValue("score", score);
        update.Parameters.AddWithValue("id", id);
        await update.ExecuteNonQueryAsync(ct);
    }
}
