using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

var jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? throw new InvalidOperationException("JWT_SECRET is not set");
var jwtIssuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "bank-auth";
var tokenMinutes = int.Parse(Environment.GetEnvironmentVariable("TOKEN_LIFETIME_MINUTES") ?? "60");
var dbPath = Environment.GetEnvironmentVariable("AUTH_DB_PATH") ?? "users.db";
var otlpEndpoint = Environment.GetEnvironmentVariable("OTLP_ENDPOINT") ?? "http://tempo:4318/v1/traces";
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auth-service"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/metrics") && !ctx.Request.Path.StartsWithSegments("/health"))
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri(otlpEndpoint);
            o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        }));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = signingKey,
            RoleClaimType = "role",
            NameClaimType = "sub",
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
var log = app.Logger;

var loginAttempts = Metrics.CreateCounter("bank_auth_login_attempts_total", "Login attempts by result", "result");
var tokensIssued = Metrics.CreateCounter("bank_auth_tokens_issued_total", "Issued access tokens by role", "role");
var usersRegistered = Metrics.CreateCounter("bank_auth_users_registered_total", "Registered users");
foreach (var r in new[] { "success", "invalid_credentials", "bad_request", "disabled" }) loginAttempts.WithLabels(r);

var roles = new[] { "admin", "operator", "analyst" };
var users = new UserStore(dbPath);
users.Initialize();
users.EnsureUser(Env("ADMIN_USERNAME", "admin"), Env("ADMIN_PASSWORD", "admin_pass_123"), "admin");
users.EnsureUser(Env("OPERATOR_USERNAME", "operator"), Env("OPERATOR_PASSWORD", "operator_pass"), "operator");
users.EnsureUser(Env("ANALYST_USERNAME", "analyst"), Env("ANALYST_PASSWORD", "analyst_pass"), "analyst");

app.UseHttpMetrics();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMetrics();

app.MapPost("/login", (LoginRequest? req) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
    {
        loginAttempts.WithLabels("bad_request").Inc();
        return Results.BadRequest(new { error = "username and password are required" });
    }

    var user = users.Find(req.Username);
    if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
    {
        loginAttempts.WithLabels("invalid_credentials").Inc();
        log.LogWarning("Login failed for {Username}", req.Username);
        return Results.Json(new { error = "invalid credentials" }, statusCode: 401);
    }

    if (!user.IsActive)
    {
        loginAttempts.WithLabels("disabled").Inc();
        log.LogWarning("Login rejected for disabled user {Username}", req.Username);
        return Results.Json(new { error = "user is disabled" }, statusCode: 403);
    }

    var expires = DateTime.UtcNow.AddMinutes(tokenMinutes);
    var token = new JwtSecurityToken(
        issuer: jwtIssuer,
        claims: new[]
        {
            new Claim("sub", user.Username),
            new Claim("role", user.Role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        },
        expires: expires,
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

    loginAttempts.WithLabels("success").Inc();
    tokensIssued.WithLabels(user.Role).Inc();
    log.LogInformation("User {Username} logged in with role {Role}", user.Username, user.Role);

    return Results.Ok(new
    {
        access_token = new JwtSecurityTokenHandler().WriteToken(token),
        token_type = "Bearer",
        expires_in = tokenMinutes * 60,
        role = user.Role
    });
});

app.MapGet("/verify", (ClaimsPrincipal user) => Results.Ok(new
{
    username = user.FindFirstValue("sub"),
    role = user.FindFirstValue("role")
})).RequireAuthorization();

app.MapPost("/register", (RegisterRequest? req) =>
{
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || req.Username.Length is < 3 or > 50)
        return Results.BadRequest(new { error = "username must be 3-50 characters" });
    if (string.IsNullOrEmpty(req.Password) || req.Password.Length is < 8 or > 128)
        return Results.BadRequest(new { error = "password must be 8-128 characters" });
    var role = string.IsNullOrWhiteSpace(req.Role) ? "operator" : req.Role;
    if (!roles.Contains(role))
        return Results.BadRequest(new { error = $"role must be one of: {string.Join(", ", roles)}" });
    if (users.Find(req.Username) is not null)
        return Results.Conflict(new { error = "user already exists" });

    users.Create(req.Username, req.Password, role);
    usersRegistered.Inc();
    log.LogInformation("User {Username} registered with role {Role}", req.Username, role);
    return Results.Created($"/users/{req.Username}", new { username = req.Username, role });
}).RequireAuthorization(p => p.RequireRole("admin"));

app.MapGet("/users", () => Results.Ok(users.List())).RequireAuthorization(p => p.RequireRole("admin"));

app.MapPost("/users/{username}/disable", (string username) =>
    users.SetActive(username, false) ? Results.Ok(new { username, active = false }) : Results.NotFound())
    .RequireAuthorization(p => p.RequireRole("admin"));

app.MapPost("/users/{username}/enable", (string username) =>
    users.SetActive(username, true) ? Results.Ok(new { username, active = true }) : Results.NotFound())
    .RequireAuthorization(p => p.RequireRole("admin"));

app.Run("http://0.0.0.0:8002");

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;

record LoginRequest(string? Username, string? Password);
record RegisterRequest(string? Username, string? Password, string? Role);
record UserRecord(string Username, string PasswordHash, string Role, bool IsActive);

static class PasswordHasher
{
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4) return false;
        var iterations = int.Parse(parts[1]);
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

class UserStore(string path)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                username TEXT PRIMARY KEY,
                password_hash TEXT NOT NULL,
                role TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """;
        cmd.ExecuteNonQuery();
    }

    public void EnsureUser(string username, string password, string role)
    {
        if (Find(username) is null) Create(username, password, role);
    }

    public void Create(string username, string password, string role)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO users (username, password_hash, role) VALUES ($u, $h, $r)";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$h", PasswordHasher.Hash(password));
        cmd.Parameters.AddWithValue("$r", role);
        cmd.ExecuteNonQuery();
    }

    public UserRecord? Find(string username)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT username, password_hash, role, is_active FROM users WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", username);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new UserRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1) : null;
    }

    public bool SetActive(string username, bool active)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET is_active = $a WHERE username = $u";
        cmd.Parameters.AddWithValue("$a", active ? 1 : 0);
        cmd.Parameters.AddWithValue("$u", username);
        return cmd.ExecuteNonQuery() == 1;
    }

    public List<object> List()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT username, role, is_active, created_at FROM users ORDER BY username";
        using var r = cmd.ExecuteReader();
        var result = new List<object>();
        while (r.Read())
            result.Add(new { username = r.GetString(0), role = r.GetString(1), active = r.GetInt64(2) == 1, created_at = r.GetString(3) });
        return result;
    }
}
