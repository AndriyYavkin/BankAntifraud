using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

namespace TransferService;

public sealed class EventPublisher(TransferSettings settings, ILogger<EventPublisher> log) : IAsyncDisposable
{
    public const string Exchange = "bank";
    public const string RoutingKey = "transfer.initiated";

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task<bool> PublishAsync(TransferInitiated evt, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity($"{Exchange} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", Exchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", RoutingKey);
        activity?.SetTag("messaging.message.id", evt.TransferId.ToString());

        var props = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = evt.TransferId.ToString(),
            Type = evt.EventType,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>()
        };

        var context = activity?.Context ?? Activity.Current?.Context ?? default;
        Propagators.DefaultTextMapPropagator.Inject(
            new PropagationContext(context, Baggage.Current),
            props.Headers,
            (headers, key, value) => headers[key] = Encoding.UTF8.GetBytes(value));

        var body = JsonSerializer.SerializeToUtf8Bytes(evt, Json);

        await _lock.WaitAsync(ct);
        try
        {
            var channel = await GetChannelAsync(ct);
            await channel.BasicPublishAsync(Exchange, RoutingKey, mandatory: true, basicProperties: props, body: body, cancellationToken: ct);
            log.LogInformation("Event {EventType} for transfer {TransferId} published to {Exchange}/{RoutingKey}", evt.EventType, evt.TransferId, Exchange, RoutingKey);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            log.LogError(ex, "Failed to publish {EventType} for transfer {TransferId}; it stays pending for fraud check", evt.EventType, evt.TransferId);
            await ResetAsync();
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await ResetAsync();

        var factory = new ConnectionFactory
        {
            HostName = settings.RabbitHost,
            VirtualHost = settings.RabbitVhost,
            UserName = settings.RabbitUser,
            Password = settings.RabbitPassword,
            ClientProvidedName = "transfer-service",
            AutomaticRecoveryEnabled = true
        };
        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        log.LogInformation("Connected to RabbitMQ {Host}/{Vhost}", settings.RabbitHost, settings.RabbitVhost);
        return _channel;
    }

    private async Task ResetAsync()
    {
        try
        {
            if (_channel is not null) await _channel.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Error while closing RabbitMQ connection");
        }
        _channel = null;
        _connection = null;
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}
