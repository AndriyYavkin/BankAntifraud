using System.Security.Cryptography;
using Npgsql;

namespace TransferService;

public sealed record AccountDto(int Id, string OwnerName, string Iban, decimal Balance, string Currency, bool IsBlocked, bool IsInternal, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record CreateAccountRequest(string? OwnerName, decimal? InitialBalance, string? Currency);

public sealed record TransferDto(
    Guid Id, int FromAccountId, int ToAccountId, decimal Amount, string Currency, string? Reference, string InitiatedBy,
    string Status, string FraudStatus, int? FraudScore, string IsolationLevel, string LockMode, int Attempts,
    DateTime CreatedAt, DateTime? CheckedAt);

public sealed record FraudAlertDto(long Id, Guid TransferId, int AccountId, string Rule, int Score, string Details, DateTime CreatedAt);

public sealed class AccountRepository(NpgsqlDataSource db)
{
    private const string AccountColumns = "id, owner_name, iban, balance, currency, is_blocked, is_internal, created_at, updated_at";
    private const string TransferColumns = "id, from_account_id, to_account_id, amount, currency, reference, initiated_by, status, fraud_status, fraud_score, isolation_level, lock_mode, attempts, created_at, checked_at";

    public async Task<List<AccountDto>> ListAsync(int limit)
    {
        await using var cmd = db.CreateCommand($"SELECT {AccountColumns} FROM accounts ORDER BY id LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit);
        return await ReadAllAsync(cmd, ReadAccount);
    }

    public async Task<AccountDto?> GetAsync(int id)
    {
        await using var cmd = db.CreateCommand($"SELECT {AccountColumns} FROM accounts WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadAllAsync(cmd, ReadAccount)).FirstOrDefault();
    }

    public async Task<AccountDto> CreateAsync(string owner, decimal balance, string currency)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO accounts (owner_name, iban, balance, currency) VALUES (@owner, @iban, @balance, @currency) RETURNING {AccountColumns}");
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("iban", GenerateIban());
        cmd.Parameters.AddWithValue("balance", balance);
        cmd.Parameters.AddWithValue("currency", currency);
        return (await ReadAllAsync(cmd, ReadAccount)).Single();
    }

    public async Task<bool> UnblockAsync(int id)
    {
        await using var cmd = db.CreateCommand("UPDATE accounts SET is_blocked = false, updated_at = now() WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync() == 1;
    }

    public async Task<List<TransferDto>> ListTransfersAsync(int? accountId, int limit)
    {
        var where = accountId is null ? "" : "WHERE from_account_id = @account OR to_account_id = @account";
        await using var cmd = db.CreateCommand($"SELECT {TransferColumns} FROM transfers {where} ORDER BY created_at DESC LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit);
        if (accountId is not null) cmd.Parameters.AddWithValue("account", accountId.Value);
        return await ReadAllAsync(cmd, ReadTransfer);
    }

    public async Task<TransferDto?> GetTransferAsync(Guid id)
    {
        await using var cmd = db.CreateCommand($"SELECT {TransferColumns} FROM transfers WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadAllAsync(cmd, ReadTransfer)).FirstOrDefault();
    }

    public async Task<List<FraudAlertDto>> ListAlertsAsync(int? accountId, int limit)
    {
        var where = accountId is null ? "" : "WHERE account_id = @account";
        await using var cmd = db.CreateCommand($"SELECT id, transfer_id, account_id, rule, score, details, created_at FROM fraud_alerts {where} ORDER BY created_at DESC LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit);
        if (accountId is not null) cmd.Parameters.AddWithValue("account", accountId.Value);
        return await ReadAllAsync(cmd, r => new FraudAlertDto(r.GetInt64(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetDateTime(6)));
    }

    public async Task<object> StatsAsync()
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM accounts),
                (SELECT count(*) FROM accounts WHERE is_blocked),
                (SELECT count(*) FROM transfers),
                (SELECT count(*) FROM transfers WHERE fraud_status = 'pending'),
                (SELECT count(*) FROM transfers WHERE fraud_status = 'suspicious'),
                (SELECT count(*) FROM fraud_alerts)
            """);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return new
        {
            accounts = r.GetInt64(0),
            blocked_accounts = r.GetInt64(1),
            transfers = r.GetInt64(2),
            pending_fraud_checks = r.GetInt64(3),
            suspicious_transfers = r.GetInt64(4),
            fraud_alerts = r.GetInt64(5)
        };
    }

    private static AccountDto ReadAccount(NpgsqlDataReader r) =>
        new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6), r.GetDateTime(7), r.GetDateTime(8));

    private static TransferDto ReadTransfer(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), r.GetDecimal(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
        r.IsDBNull(9) ? null : r.GetInt32(9), r.GetString(10), r.GetString(11), r.GetInt32(12),
        r.GetDateTime(13), r.IsDBNull(14) ? null : r.GetDateTime(14));

    private static async Task<List<T>> ReadAllAsync<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> map)
    {
        var list = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(map(r));
        return list;
    }

    private static string GenerateIban()
    {
        var digits = string.Concat(Enumerable.Range(0, 27).Select(_ => RandomNumberGenerator.GetInt32(10)));
        return "UA" + digits;
    }
}

public sealed class BalanceMonitor(NpgsqlDataSource db, ILogger<BalanceMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using (var cmd = db.CreateCommand("SELECT currency, sum(balance) FROM accounts GROUP BY currency"))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                        AppMetrics.TotalBalance.WithLabels(r.GetString(0)).Set((double)r.GetDecimal(1));
                }

                await using (var cmd = db.CreateCommand(
                    "SELECT (SELECT count(*) FROM accounts WHERE is_blocked), (SELECT count(*) FROM transfers WHERE fraud_status = 'pending')"))
                await using (var r = await cmd.ExecuteReaderAsync(ct))
                {
                    await r.ReadAsync(ct);
                    AppMetrics.BlockedAccounts.Set(r.GetInt64(0));
                    AppMetrics.PendingFraudChecks.Set(r.GetInt64(1));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Balance monitor query failed: {Error}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
