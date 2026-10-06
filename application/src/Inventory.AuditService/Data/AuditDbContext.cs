using Inventory.Shared.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Inventory.AuditService.Data;

/// <summary>Запись централизованного журнала аудита (SR-02: «отдельная база данных»).</summary>
public sealed class AuditRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Service { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
    public string? ActorLogin { get; set; }
    public string? ActorRole { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string Result { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public List<string> Flags { get; set; } = [];
    public string? Details { get; set; }
    public string? SourceIp { get; set; }

    public static AuditRecord From(AuditEvent e, DateTimeOffset receivedAt) => new()
    {
        Id = e.Id,
        OccurredAt = e.OccurredAt.ToUniversalTime(),
        ReceivedAt = receivedAt,
        Service = Limit(e.Service, 50),
        ActorId = e.ActorId,
        ActorLogin = LimitOrNull(e.ActorLogin, 64),
        ActorRole = LimitOrNull(e.ActorRole, 20),
        Action = Limit(e.Action, 100),
        TargetType = LimitOrNull(e.TargetType, 50),
        TargetId = LimitOrNull(e.TargetId, 200),
        Result = Limit(e.Result, 20),
        Severity = Limit(e.Severity, 20),
        Flags = e.Flags.Select(f => Limit(f, 50)).ToList(),
        Details = LimitOrNull(e.Details, 2000),
        SourceIp = LimitOrNull(e.SourceIp, 64)
    };

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? LimitOrNull(string? value, int max) => value is null ? null : Limit(value, max);
}

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditRecord>(e =>
        {
            e.ToTable("audit_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Service).HasMaxLength(50);
            e.Property(x => x.ActorLogin).HasMaxLength(64);
            e.Property(x => x.ActorRole).HasMaxLength(20);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.TargetType).HasMaxLength(50);
            e.Property(x => x.TargetId).HasMaxLength(200);
            e.Property(x => x.Result).HasMaxLength(20);
            e.Property(x => x.Severity).HasMaxLength(20);
            e.Property(x => x.Details).HasMaxLength(2000);
            e.Property(x => x.SourceIp).HasMaxLength(64);
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.Severity, x.OccurredAt });
            e.HasIndex(x => x.ActorId);
        });
    }
}
