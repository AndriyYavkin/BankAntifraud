using System.Diagnostics;
using Npgsql;

namespace TransferService;

public sealed record TransferRequest(int? FromAccountId, int? ToAccountId, decimal? Amount, string? Reference);

public sealed record ExecutionResult(int StatusCode, object Body);

public sealed record TransferInitiated(
    string EventType,
    Guid TransferId,
    int FromAccountId,
    int ToAccountId,
    decimal Amount,
    string Currency,
    decimal FromBalanceAfter,
    string InitiatedBy,
    DateTime CreatedAt);

internal sealed record AccountRow(int Id, decimal Balance, string Currency, bool IsBlocked);

internal sealed record TxResult(bool Committed, ExecutionResult Result, TransferInitiated? Event);

public sealed class TransferExecutor(NpgsqlDataSource db, EventPublisher publisher, ILogger<TransferExecutor> log)
{
    private const decimal MaxAmount = 1_000_000m;

    public async Task<ExecutionResult> ExecuteAsync(TransferRequest req, TransferOptions o, string user, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("transfer.execute");
        activity?.SetTag("transfer.isolation", o.IsolationName);
        activity?.SetTag("transfer.lock_mode", o.LockModeName);
        activity?.SetTag("transfer.lock_hold_ms", o.LockHoldMs);
        activity?.SetTag("transfer.from_account_id", req.FromAccountId);
        activity?.SetTag("transfer.to_account_id", req.ToAccountId);
        activity?.SetTag("transfer.amount", req.Amount?.ToString());
        activity?.SetTag("enduser.id", user);

        if (req.FromAccountId is null || req.ToAccountId is null)
            return Reject(activity, "invalid_request", 400, "from_account_id and to_account_id are required", req);
        if (req.Amount is not { } amount || amount <= 0 || amount > MaxAmount || decimal.Round(amount, 2) != amount)
            return Reject(activity, "invalid_amount", 400, $"amount must be positive, at most {MaxAmount} and have no more than 2 decimal places", req);
        if (req.FromAccountId == req.ToAccountId)
            return Reject(activity, "same_account", 400, "cannot transfer to the same account", req);

        for (var attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var tx = await RunTransactionAsync(req.FromAccountId.Value, req.ToAccountId.Value, amount, req.Reference, user, o, attempt, ct);
                AppMetrics.TransactionDuration.WithLabels(o.IsolationName, o.LockModeName, tx.Committed ? "committed" : "rejected").Observe(sw.Elapsed.TotalSeconds);

                if (!tx.Committed)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "rejected");
                    return tx.Result;
                }

                AppMetrics.Transfers.WithLabels("completed").Inc();
                AppMetrics.Amount.WithLabels(tx.Event!.Currency).Observe((double)amount);
                activity?.SetTag("transfer.id", tx.Event.TransferId.ToString());
                activity?.SetTag("transfer.attempts", attempt);
                log.LogInformation(
                    "Transfer {TransferId} completed: account {FromAccountId} -> {ToAccountId}, amount {Amount} {Currency}, isolation {Isolation}, lock mode {LockMode}, attempts {Attempts}, duration {DurationMs} ms",
                    tx.Event.TransferId, tx.Event.FromAccountId, tx.Event.ToAccountId, amount, tx.Event.Currency, o.IsolationName, o.LockModeName, attempt, sw.ElapsedMilliseconds);

                var published = await publisher.PublishAsync(tx.Event, ct);
                AppMetrics.EventsPublished.WithLabels(published ? "ok" : "failed").Inc();
                return tx.Result;
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
            {
                var type = ex.SqlState == PostgresErrorCodes.DeadlockDetected ? "deadlock" : "serialization";
                AppMetrics.TransactionDuration.WithLabels(o.IsolationName, o.LockModeName, type).Observe(sw.Elapsed.TotalSeconds);
                AppMetrics.Conflicts.WithLabels(type, o.IsolationName, o.LockModeName).Inc();
                activity?.AddEvent(new ActivityEvent($"db.{type}", tags: new ActivityTagsCollection { ["attempt"] = attempt, ["sqlstate"] = ex.SqlState }));
                log.LogWarning(
                    "Transfer conflict {ConflictType} (SQLSTATE {SqlState}) on attempt {Attempt}: account {FromAccountId} -> {ToAccountId}, amount {Amount}, isolation {Isolation}, lock mode {LockMode}, duration {DurationMs} ms. {Detail}",
                    type, ex.SqlState, attempt, req.FromAccountId, req.ToAccountId, amount, o.IsolationName, o.LockModeName, sw.ElapsedMilliseconds, ex.MessageText);

                if (attempt <= o.MaxRetries)
                {
                    AppMetrics.Retries.WithLabels(type).Inc();
                    await Task.Delay(Random.Shared.Next(20, 120), ct);
                    continue;
                }

                AppMetrics.Transfers.WithLabels("conflict").Inc();
                activity?.SetStatus(ActivityStatusCode.Error, type);
                return new ExecutionResult(409, new
                {
                    error = type == "deadlock" ? "deadlock_detected" : "serialization_failure",
                    sqlstate = ex.SqlState,
                    retryable = true,
                    attempts = attempt
                });
            }
            catch (NpgsqlException ex) when (ex is not PostgresException || ((PostgresException)ex).SqlState.StartsWith("57P") || ((PostgresException)ex).SqlState.StartsWith("08"))
            {
                AppMetrics.TransactionDuration.WithLabels(o.IsolationName, o.LockModeName, "error").Observe(sw.Elapsed.TotalSeconds);
                AppMetrics.Transfers.WithLabels("error").Inc();
                activity?.SetStatus(ActivityStatusCode.Error, "database unavailable");
                log.LogError("Database unavailable, transfer {FromAccountId} -> {ToAccountId} not executed: {Error}", req.FromAccountId, req.ToAccountId, ex.Message);
                return new ExecutionResult(503, new { error = "database_unavailable" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppMetrics.TransactionDuration.WithLabels(o.IsolationName, o.LockModeName, "error").Observe(sw.Elapsed.TotalSeconds);
                AppMetrics.Transfers.WithLabels("error").Inc();
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                log.LogError(ex, "Transfer failed: account {FromAccountId} -> {ToAccountId}, amount {Amount}", req.FromAccountId, req.ToAccountId, amount);
                return new ExecutionResult(500, new { error = "internal_error" });
            }
        }
    }

    private async Task<TxResult> RunTransactionAsync(int fromId, int toId, decimal amount, string? reference, string user, TransferOptions o, int attempt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(o.Isolation, ct);

        var (firstId, secondId) = o.LockMode == LockMode.Ordered
            ? (Math.Min(fromId, toId), Math.Max(fromId, toId))
            : (fromId, toId);
        var lockRows = o.LockMode != LockMode.None;

        var first = await ReadAccountAsync(conn, tx, firstId, lockRows, ct);
        if (o.LockHoldMs > 0) await Task.Delay(o.LockHoldMs, ct);
        var second = await ReadAccountAsync(conn, tx, secondId, lockRows, ct);

        var source = first?.Id == fromId ? first : second;
        var target = first?.Id == toId ? first : second;
        if (source?.Id != fromId) source = null;
        if (target?.Id != toId) target = null;

        var req = new TransferRequest(fromId, toId, amount, reference);

        if (source is null || target is null)
            return Rejected(Reject(null, "account_not_found", 404, $"account {(source is null ? fromId : toId)} does not exist", req));
        if (source.IsBlocked || target.IsBlocked)
            return Rejected(Reject(null, "account_blocked", 403, $"account {(source.IsBlocked ? fromId : toId)} is blocked by anti-fraud", req));
        if (source.Currency != target.Currency)
            return Rejected(Reject(null, "currency_mismatch", 422, $"currency mismatch: {source.Currency} -> {target.Currency}", req));
        if (source.Balance < amount)
            return Rejected(Reject(null, "insufficient_funds", 422, $"insufficient funds: balance {source.Balance}, requested {amount}", req));

        decimal fromAfter, toAfter;
        if (o.LockMode == LockMode.None)
        {
            fromAfter = source.Balance - amount;
            toAfter = target.Balance + amount;
            await SetBalanceAsync(conn, tx, fromId, fromAfter, ct);
            await SetBalanceAsync(conn, tx, toId, toAfter, ct);
        }
        else
        {
            fromAfter = await AddBalanceAsync(conn, tx, fromId, -amount, ct);
            toAfter = await AddBalanceAsync(conn, tx, toId, amount, ct);
        }

        var id = Guid.NewGuid();
        DateTime createdAt;
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO transfers (id, from_account_id, to_account_id, amount, currency, reference, initiated_by, from_balance_after, isolation_level, lock_mode, attempts)
            VALUES (@id, @from, @to, @amount, @currency, @reference, @user, @fromAfter, @isolation, @lockMode, @attempts)
            RETURNING created_at
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("from", fromId);
            insert.Parameters.AddWithValue("to", toId);
            insert.Parameters.AddWithValue("amount", amount);
            insert.Parameters.AddWithValue("currency", source.Currency);
            insert.Parameters.AddWithValue("reference", (object?)reference ?? DBNull.Value);
            insert.Parameters.AddWithValue("user", user);
            insert.Parameters.AddWithValue("fromAfter", fromAfter);
            insert.Parameters.AddWithValue("isolation", o.IsolationName);
            insert.Parameters.AddWithValue("lockMode", o.LockModeName);
            insert.Parameters.AddWithValue("attempts", attempt);
            createdAt = (DateTime)(await insert.ExecuteScalarAsync(ct))!;
        }

        await tx.CommitAsync(ct);

        var evt = new TransferInitiated("TransferInitiated", id, fromId, toId, amount, source.Currency, fromAfter, user, createdAt);
        var body = new
        {
            id,
            from_account_id = fromId,
            to_account_id = toId,
            amount,
            currency = source.Currency,
            status = "completed",
            fraud_status = "pending",
            from_balance_after = fromAfter,
            to_balance_after = toAfter,
            isolation_level = o.IsolationName,
            lock_mode = o.LockModeName,
            attempts = attempt,
            created_at = createdAt
        };
        return new TxResult(true, new ExecutionResult(201, body), evt);
    }

    private static TxResult Rejected(ExecutionResult r) => new(false, r, null);

    private ExecutionResult Reject(Activity? activity, string reason, int status, string message, TransferRequest req)
    {
        AppMetrics.ValidationErrors.WithLabels(reason).Inc();
        AppMetrics.Transfers.WithLabels("rejected").Inc();
        activity?.SetTag("transfer.rejected_reason", reason);
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        Activity.Current?.SetTag("transfer.rejected_reason", reason);
        log.LogWarning("Transfer rejected {Reason}: account {FromAccountId} -> {ToAccountId}, amount {Amount}. {Detail}",
            reason, req.FromAccountId, req.ToAccountId, req.Amount, message);
        return new ExecutionResult(status, new { error = reason, message });
    }

    private static async Task<AccountRow?> ReadAccountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int id, bool forUpdate, CancellationToken ct)
    {
        var sql = "SELECT id, balance, currency, is_blocked FROM accounts WHERE id = @id" + (forUpdate ? " FOR UPDATE" : "");
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new AccountRow(r.GetInt32(0), r.GetDecimal(1), r.GetString(2), r.GetBoolean(3)) : null;
    }

    private static async Task<decimal> AddBalanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int id, decimal delta, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE accounts SET balance = balance + @delta, updated_at = now() WHERE id = @id RETURNING balance", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("delta", delta);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task SetBalanceAsync(NpgsqlConnection conn, NpgsqlTransaction tx, int id, decimal balance, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE accounts SET balance = @balance, updated_at = now() WHERE id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("balance", balance);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
