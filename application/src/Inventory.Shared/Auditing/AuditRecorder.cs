using System.Text.Json;
using Inventory.Shared.Http;
using Inventory.Shared.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Inventory.Shared.Auditing;

public sealed class AuditOptions
{
    public string ServiceName { get; set; } = "unknown";
    public string Topic { get; set; } = "audit-events";
}

public interface IAuditRecorder
{
    /// <summary>
    /// Добавляет событие в локальный журнал (outbox) текущего DbContext без сохранения.
    /// Событие сохраняется вызывающим кодом в той же транзакции, что и предметное изменение.
    /// </summary>
    AuditEvent Record(AuditEntry entry);

    /// <summary>Добавляет событие и сразу сохраняет его.</summary>
    Task<AuditEvent> RecordNowAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed class AuditRecorder<TContext>(
    TContext db,
    IHttpContextAccessor httpContextAccessor,
    WorkingHoursPolicy workingHours,
    TimeProvider time,
    IOptions<AuditOptions> options) : IAuditRecorder
    where TContext : DbContext, IOutboxStore
{
    public AuditEvent Record(AuditEntry entry)
    {
        var http = httpContextAccessor.HttpContext;
        var user = http?.User;

        var actor = entry.Actor ?? (user is { Identity.IsAuthenticated: true }
            ? new AuditActor(user.TryGetUserId(), user.GetLogin(), user.GetRole())
            : null);

        var now = time.GetUtcNow();
        var flags = new List<string>(entry.Flags ?? []);
        var severity = entry.Severity ?? AuditSeverity.Info;

        // SR-02: любые действия во внеучебное время помечаются предупреждением.
        if (workingHours.IsOffHours(now))
        {
            flags.Add(AuditFlags.OffHours);
            severity = AuditSeverity.Warning;
        }

        var auditEvent = new AuditEvent
        {
            OccurredAt = now,
            Service = options.Value.ServiceName,
            ActorId = actor?.Id,
            ActorLogin = actor?.Login,
            ActorRole = actor?.Role,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Result = entry.Result,
            Severity = severity,
            Flags = flags.Distinct().ToList(),
            Details = entry.Details,
            SourceIp = http?.Connection.RemoteIpAddress?.ToString()
        };

        db.AuditOutbox.Add(new OutboxMessage
        {
            EventId = auditEvent.Id,
            Topic = options.Value.Topic,
            Action = auditEvent.Action,
            Severity = auditEvent.Severity,
            Payload = JsonSerializer.Serialize(auditEvent, JsonDefaults.Options),
            CreatedAt = now
        });

        return auditEvent;
    }

    public async Task<AuditEvent> RecordNowAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var auditEvent = Record(entry);
        await db.SaveChangesAsync(cancellationToken);
        return auditEvent;
    }
}
