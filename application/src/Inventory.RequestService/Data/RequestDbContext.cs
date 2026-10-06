using System.ComponentModel.DataAnnotations;
using Inventory.Shared.Auditing;
using Inventory.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace Inventory.RequestService.Data;

public enum RequestStatus
{
    /// <summary>Ожидает рецензии учителя.</summary>
    Pending,

    /// <summary>Одобрена, устройство выдано ученику.</summary>
    Approved,

    /// <summary>Отклонена учителем.</summary>
    Rejected,

    /// <summary>Устройство возвращено, заявка закрыта.</summary>
    Returned
}

public sealed class LoanRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid StudentId { get; set; }
    public string StudentLogin { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;

    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public DeviceType DeviceType { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Guid? ReviewerId { get; set; }
    public string? ReviewerName { get; set; }
    public string? ReviewComment { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    public Guid? ReturnConfirmedById { get; set; }
    public string? ReturnConfirmedByName { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }

    /// <summary>Токен оптимистичной блокировки (системный столбец xmin PostgreSQL).</summary>
    [Timestamp]
    public uint Version { get; set; }
}

public sealed class RequestDbContext(DbContextOptions<RequestDbContext> options) : DbContext(options), IOutboxStore
{
    public DbSet<LoanRequest> LoanRequests => Set<LoanRequest>();
    public DbSet<OutboxMessage> AuditOutbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoanRequest>(e =>
        {
            e.ToTable("loan_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.StudentLogin).HasMaxLength(32);
            e.Property(x => x.StudentName).HasMaxLength(200);
            e.Property(x => x.DeviceName).HasMaxLength(200);
            e.Property(x => x.DeviceType).HasConversion<string>().HasMaxLength(40);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Comment).HasMaxLength(500);
            e.Property(x => x.ReviewerName).HasMaxLength(200);
            e.Property(x => x.ReviewComment).HasMaxLength(500);
            e.Property(x => x.ReturnConfirmedByName).HasMaxLength(200);

            e.HasIndex(x => new { x.StudentId, x.Status });
            e.HasIndex(x => new { x.DeviceId, x.Status });

            // Одно устройство может быть выдано только по одной активной заявке.
            e.HasIndex(x => x.DeviceId)
                .IsUnique()
                .HasFilter("status = 'Approved'")
                .HasDatabaseName("ux_loan_requests_active_device");
        });

        modelBuilder.ConfigureAuditOutbox();
    }
}
