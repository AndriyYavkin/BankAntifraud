using FraudWorker;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

var settings = WorkerSettings.FromEnvironment();

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(settings.DatabaseUrl).Build());
builder.Services.AddSingleton<FraudAnalyzer>();
builder.Services.AddHostedService<FraudConsumer>();
builder.Services.AddHostedService<PendingSweeper>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("fraud-worker"))
    .WithTracing(t => t
        .AddSource(WorkerTelemetry.SourceName)
        .AddNpgsql()
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(settings.OtlpEndpoint);
            o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        }));

var app = builder.Build();

WorkerMetrics.Initialize();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMetrics();

app.Run("http://0.0.0.0:8001");
