using Inventory.AuditService.Data;
using Inventory.AuditService.Services;
using Inventory.Shared.Auditing;
using Inventory.Shared.Hosting;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("audit-service");

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Не задана строка подключения ConnectionStrings:Default.");
builder.Services.AddDbContext<AuditDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

if (builder.Configuration.GetValue("Kafka:Enabled", true))
{
    builder.Services.AddHostedService<AuditConsumer>();
}

var app = builder.Build();

app.UseServiceDefaults();

// Просмотр журнала доступен только администратору.
var audit = app.MapGroup("/audit").WithTags("Audit").RequireAuthorization(Policies.Admin);

audit.MapGet("/", (string? severity, string? action, string? service, Guid? actorId,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, AuditDbContext db, CancellationToken ct) =>
    QueryAsync(db, severity, action, service, actorId, from, to, limit, ct));

audit.MapGet("/warnings", (DateTimeOffset? from, DateTimeOffset? to, int? limit, AuditDbContext db, CancellationToken ct) =>
    QueryAsync(db, AuditSeverity.Warning, null, null, null, from, to, limit, ct));

await app.InitializeDatabaseAsync<AuditDbContext>();
await app.RunAsync();

static async Task<IResult> QueryAsync(
    AuditDbContext db, string? severity, string? action, string? service, Guid? actorId,
    DateTimeOffset? from, DateTimeOffset? to, int? limit, CancellationToken ct)
{
    var take = limit ?? 100;
    if (take is < 1 or > 1000)
    {
        return ApiErrors.BadRequest("invalid_limit", "Параметр limit должен быть в диапазоне 1..1000.");
    }

    var query = db.AuditRecords.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(severity)) query = query.Where(r => r.Severity == severity);
    if (!string.IsNullOrWhiteSpace(action)) query = query.Where(r => r.Action == action);
    if (!string.IsNullOrWhiteSpace(service)) query = query.Where(r => r.Service == service);
    if (actorId is { } id) query = query.Where(r => r.ActorId == id);
    if (from is { } f) { var fu = f.ToUniversalTime(); query = query.Where(r => r.OccurredAt >= fu); }
    if (to is { } t) { var tu = t.ToUniversalTime(); query = query.Where(r => r.OccurredAt <= tu); }

    var records = await query.OrderByDescending(r => r.OccurredAt).Take(take).ToListAsync(ct);
    return Results.Ok(records);
}
