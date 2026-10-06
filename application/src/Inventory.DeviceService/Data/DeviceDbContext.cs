using Inventory.Shared.Auditing;
using Inventory.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.DeviceService.Data;

public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DeviceType Type { get; set; }
    public string? Description { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Available;

    /// <summary>Текущий владелец (ученик). Заполнен только при статусе <see cref="DeviceStatus.Issued"/>.</summary>
    public Guid? HolderId { get; set; }

    public string? HolderName { get; set; }
    public Guid? IssuedByRequestId { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DeviceDbContext(DbContextOptions<DeviceDbContext> options) : DbContext(options), IOutboxStore
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<OutboxMessage> AuditOutbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("devices", t =>
            {
                // Согласованность статуса и владельца (threat-model.md, «Устройства»).
                t.HasCheckConstraint("ck_devices_holder_matches_status",
                    "(status = 'Issued' AND holder_id IS NOT NULL) OR (status <> 'Issued' AND holder_id IS NULL)");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.HolderName).HasMaxLength(200);
            e.HasIndex(x => x.Status);

            // SR-08 на уровне БД: у одного владельца не может быть двух устройств одного типа,
            // даже если проверка в коде будет обойдена или ошибётся.
            e.HasIndex(x => new { x.HolderId, x.Type })
                .IsUnique()
                .HasFilter("holder_id IS NOT NULL")
                .HasDatabaseName("ux_devices_holder_type");
        });

        modelBuilder.ConfigureAuditOutbox();
    }
}
