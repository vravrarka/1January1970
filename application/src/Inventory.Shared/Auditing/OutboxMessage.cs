using Microsoft.EntityFrameworkCore;

namespace Inventory.Shared.Auditing;

/// <summary>
/// Локальная копия события аудита в базе сервиса (паттерн Transactional Outbox).
/// Запись создаётся в той же транзакции, что и изменение предметных данных, поэтому
/// событие не теряется при недоступности Kafka (SR-03, T-03). После отправки в Kafka
/// запись не удаляется, а помечается <see cref="PublishedAt"/>. Так у сервиса остаётся
/// локальный журнал, а неотправленные записи образуют «журнал действий с момента
/// последнего подключения к серверу».
/// </summary>
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Severity { get; set; } = AuditSeverity.Info;
    public string Payload { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public interface IOutboxStore
{
    DbSet<OutboxMessage> AuditOutbox { get; }
}

public static class OutboxModelBuilderExtensions
{
    public static ModelBuilder ConfigureAuditOutbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("audit_outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Topic).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.Severity).HasMaxLength(20);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => x.PublishedAt);
        });

        return modelBuilder;
    }
}
