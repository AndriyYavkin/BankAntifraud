using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using TransferService;

var builder = WebApplication.CreateBuilder(args);
 
var settings = TransferSettings.FromEnvironment();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(settings.DatabaseUrl).Build());
builder.Services.AddSingleton<EventPublisher>();
builder.Services.AddSingleton<TransferExecutor>();
builder.Services.AddSingleton<AccountRepository>();
builder.Services.AddHostedService<BalanceMonitor>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("transfer-service"))
    .WithTracing(t => t
        .AddSource(Telemetry.SourceName)
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => ctx.Request.Path.StartsWithSegments("/api"))
        .AddNpgsql()
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(settings.OtlpEndpoint);
            o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        }));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.JwtIssuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtSecret)),
            RoleClaimType = "role",
            NameClaimType = "sub",
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        o.Events = new JwtBearerEvents
        {
            OnChallenge = _ =>
            {
                AppMetrics.AccessDenied.WithLabels("unauthorized").Inc();
                return Task.CompletedTask;
            },
            OnForbidden = _ =>
            {
                AppMetrics.AccessDenied.WithLabels("forbidden").Inc();
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("transfer", p => p.RequireRole("operator", "admin"))
    .AddPolicy("read", p => p.RequireRole("operator", "analyst", "admin"))
    .AddPolicy("fraud", p => p.RequireRole("analyst", "admin"))
    .AddPolicy("admin", p => p.RequireRole("admin"));

var app = builder.Build();

AppMetrics.Initialize();

app.UseHttpMetrics();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMetrics();

var api = app.MapGroup("/api");

api.MapGet("/accounts", async (AccountRepository repo, int? limit) =>
    Results.Ok(await repo.ListAsync(Math.Clamp(limit ?? 100, 1, 500))))
    .RequireAuthorization("read");

api.MapGet("/accounts/{id:int}", async (int id, AccountRepository repo) =>
    await repo.GetAsync(id) is { } account ? Results.Ok(account) : Results.NotFound(new { error = "account_not_found" }))
    .RequireAuthorization("read");

api.MapPost("/accounts", async (CreateAccountRequest? req, AccountRepository repo, ILogger<Program> log) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.OwnerName) || req.OwnerName.Length > 100)
        return Results.BadRequest(new { error = "owner_name must be 1-100 characters" });
    if (req.InitialBalance is < 0 or > 10_000_000)
        return Results.BadRequest(new { error = "initial_balance must be between 0 and 10000000" });
    var currency = (req.Currency ?? "UAH").ToUpperInvariant();
    if (currency is not ("UAH" or "USD" or "EUR"))
        return Results.BadRequest(new { error = "currency must be UAH, USD or EUR" });

    var account = await repo.CreateAsync(req.OwnerName.Trim(), req.InitialBalance ?? 0, currency);
    AppMetrics.AccountsCreated.Inc();
    log.LogInformation("Account {AccountId} created for {Owner} with balance {Balance} {Currency}", account.Id, account.OwnerName, account.Balance, account.Currency);
    return Results.Created($"/api/accounts/{account.Id}", account);
}).RequireAuthorization("transfer");

api.MapPost("/accounts/{id:int}/unblock", async (int id, AccountRepository repo, ClaimsPrincipal user, ILogger<Program> log) =>
{
    if (!await repo.UnblockAsync(id)) return Results.NotFound(new { error = "account_not_found" });
    log.LogInformation("Account {AccountId} unblocked by {User}", id, user.FindFirstValue("sub"));
    return Results.Ok(new { id, is_blocked = false });
}).RequireAuthorization("admin");

api.MapPost("/transfers", async (TransferRequest? req, HttpRequest http, TransferExecutor executor, ClaimsPrincipal user, CancellationToken ct) =>
{
    if (req is null) return Results.BadRequest(new { error = "body is required" });

    var options = TransferOptions.Resolve(settings, http.Headers);
    if (options.Error is not null) return Results.BadRequest(new { error = options.Error });

    var result = await executor.ExecuteAsync(req, options, user.FindFirstValue("sub") ?? "unknown", ct);
    return Results.Json(result.Body, statusCode: result.StatusCode);
}).RequireAuthorization("transfer");

api.MapGet("/transfers", async (AccountRepository repo, [FromQuery(Name = "account_id")] int? accountId, int? limit) =>
    Results.Ok(await repo.ListTransfersAsync(accountId, Math.Clamp(limit ?? 50, 1, 500))))
    .RequireAuthorization("read");

api.MapGet("/transfers/{id:guid}", async (Guid id, AccountRepository repo) =>
    await repo.GetTransferAsync(id) is { } t ? Results.Ok(t) : Results.NotFound(new { error = "transfer_not_found" }))
    .RequireAuthorization("read");

api.MapGet("/fraud-alerts", async (AccountRepository repo, [FromQuery(Name = "account_id")] int? accountId, int? limit) =>
    Results.Ok(await repo.ListAlertsAsync(accountId, Math.Clamp(limit ?? 50, 1, 500))))
    .RequireAuthorization("fraud");

api.MapGet("/stats", async (AccountRepository repo) => Results.Ok(await repo.StatsAsync()))
    .RequireAuthorization("read");

app.Run("http://0.0.0.0:8000");
