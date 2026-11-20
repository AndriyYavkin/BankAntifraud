using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace FraudWorker;

public sealed class FraudConsumer(WorkerSettings s, FraudAnalyzer analyzer, ILogger<FraudConsumer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                WorkerMetrics.ConsumerConnected.Set(0);
                log.LogWarning("RabbitMQ consumer stopped: {Error}. Reconnecting in 5 s", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = s.RabbitHost,
            VirtualHost = s.RabbitVhost,
            UserName = s.RabbitUser,
            Password = s.RabbitPassword,
            ClientProvidedName = "fraud-worker",
            AutomaticRecoveryEnabled = false
        };

        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.BasicQosAsync(0, s.Prefetch, false, ct);

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.ConnectionShutdownAsync += (_, e) =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => HandleAsync(channel, ea, ct);
        await channel.BasicConsumeAsync(s.Queue, autoAck: false, consumer: consumer, cancellationToken: ct);

        WorkerMetrics.ConsumerConnected.Set(1);
        log.LogInformation("Consuming queue {Queue} on {Host}/{Vhost} with prefetch {Prefetch}", s.Queue, s.RabbitHost, s.RabbitVhost, s.Prefetch);

        await closed.Task.WaitAsync(ct);
        throw new InvalidOperationException("RabbitMQ connection closed");
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var parent = Propagators.DefaultTextMapPropagator.Extract(default, ea.BasicProperties.Headers, ReadHeader);
        Baggage.Current = parent.Baggage;

        using var activity = WorkerTelemetry.Source.StartActivity($"{s.Queue} process", ActivityKind.Consumer, parent.ActivityContext);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.source.name", s.Queue);
        activity?.SetTag("messaging.message.id", ea.BasicProperties.MessageId);
        activity?.SetTag("messaging.rabbitmq.redelivered", ea.Redelivered);

        TransferInitiated? evt;
        try
        {
            evt = JsonSerializer.Deserialize<TransferInitiated>(ea.Body.Span, TransferInitiated.Json);
            if (evt is null || evt.TransferId == Guid.Empty || evt.EventType != "TransferInitiated")
                throw new JsonException("message is not a valid TransferInitiated event");
        }
        catch (JsonException ex)
        {
            WorkerMetrics.Checks.WithLabels("invalid").Inc();
            activity?.SetStatus(ActivityStatusCode.Error, "invalid message");
            log.LogError("Invalid message {MessageId} sent to dead-letter queue: {Error}. Body: {Body}",
                ea.BasicProperties.MessageId, ex.Message, Encoding.UTF8.GetString(ea.Body.Span));
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct);
            return;
        }

        activity?.SetTag("transfer.id", evt.TransferId.ToString());
        activity?.SetTag("transfer.from_account_id", evt.FromAccountId);
        activity?.SetTag("transfer.to_account_id", evt.ToAccountId);
        activity?.SetTag("transfer.amount", evt.Amount.ToString());

        var sw = Stopwatch.StartNew();
        try
        {
            if (s.AnalysisDelayMs > 0) await Task.Delay(s.AnalysisDelayMs, ct);

            var result = await analyzer.AnalyzeAsync(evt, ct);
            WorkerMetrics.AnalysisDuration.Observe(sw.Elapsed.TotalSeconds);

            switch (result.Status)
            {
                case AnalysisStatus.Duplicate:
                    WorkerMetrics.Checks.WithLabels("duplicate").Inc();
                    log.LogInformation("Transfer {TransferId} already analyzed, duplicate event skipped", evt.TransferId);
                    break;

                case AnalysisStatus.TransferMissing:
                    WorkerMetrics.Checks.WithLabels("invalid").Inc();
                    activity?.SetStatus(ActivityStatusCode.Error, "transfer not found");
                    log.LogError("Transfer {TransferId} from event not found in database, message sent to dead-letter queue", evt.TransferId);
                    await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: false, ct);
                    return;

                case AnalysisStatus.Whitelisted:
                    WorkerMetrics.Checks.WithLabels("whitelisted").Inc();
                    activity?.SetTag("fraud.result", "whitelisted");
                    log.LogInformation("Transfer {TransferId} between internal settlement accounts {AccountId} -> {ToAccountId} skipped by anti-fraud", evt.TransferId, evt.FromAccountId, evt.ToAccountId);
                    break;

                default:
                    Record(evt, result, activity, sw.ElapsedMilliseconds);
                    break;
            }

            await channel.BasicAckAsync(ea.DeliveryTag, false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WorkerMetrics.Checks.WithLabels("error").Inc();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            log.LogError("Analysis of transfer {TransferId} failed, message returned to queue: {Error}", evt.TransferId, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            await channel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, ct);
        }
    }

    private void Record(TransferInitiated evt, AnalysisResult result, Activity? activity, long elapsedMs)
    {
        var lag = (DateTime.UtcNow - DateTime.SpecifyKind(evt.CreatedAt, DateTimeKind.Utc)).TotalSeconds;
        WorkerMetrics.EventLag.Observe(Math.Max(0, lag));
        WorkerMetrics.Score.Observe(result.Score);
        foreach (var hit in result.Hits) WorkerMetrics.RuleHits.WithLabels(hit.Rule).Inc();
        WorkerMetrics.Checks.WithLabels(result.Suspicious ? "suspicious" : "clean").Inc();

        var rules = result.Hits.Count == 0 ? "none" : string.Join(",", result.Hits.Select(h => h.Rule));
        activity?.SetTag("fraud.score", result.Score);
        activity?.SetTag("fraud.result", result.Suspicious ? "suspicious" : "clean");
        activity?.SetTag("fraud.rules", rules);

        if (result.Suspicious)
        {
            log.LogWarning(
                "Suspicious transfer {TransferId}: account {AccountId} -> {ToAccountId}, amount {Amount} {Currency}, score {FraudScore}, rules {Rules}. {Details}",
                evt.TransferId, evt.FromAccountId, evt.ToAccountId, evt.Amount, evt.Currency, result.Score, rules,
                string.Join("; ", result.Hits.Select(h => h.Details)));
        }
        else
        {
            log.LogInformation(
                "Transfer {TransferId} clean: account {AccountId} -> {ToAccountId}, amount {Amount} {Currency}, score {FraudScore}, rules {Rules}, analysis {DurationMs} ms, lag {LagMs} ms",
                evt.TransferId, evt.FromAccountId, evt.ToAccountId, evt.Amount, evt.Currency, result.Score, rules, elapsedMs, (long)(lag * 1000));
        }

        if (result.AccountBlocked)
        {
            WorkerMetrics.AccountsBlocked.Inc();
            activity?.AddEvent(new ActivityEvent("account.blocked"));
            log.LogWarning("Account {AccountId} blocked automatically: fraud score {FraudScore} >= {BlockScore}", evt.FromAccountId, result.Score, s.BlockScore);
        }
    }

    private static IEnumerable<string> ReadHeader(IDictionary<string, object?>? headers, string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value) || value is null) return Array.Empty<string>();
        return value switch
        {
            byte[] bytes => new[] { Encoding.UTF8.GetString(bytes) },
            string str => new[] { str },
            _ => Array.Empty<string>()
        };
    }
}
