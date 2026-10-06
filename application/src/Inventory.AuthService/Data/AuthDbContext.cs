using Inventory.Shared.Auditing;
using Microsoft.EntityFrameworkCore;

namespace Inventory.AuthService.Data;

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Login { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
}

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options), IOutboxStore
{
    public DbSet<User> Users => Set<User>();
    public DbSet<OutboxMessage> AuditOutbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Login).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Login).IsUnique();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(20).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
        });

        modelBuilder.ConfigureAuditOutbox();
    }
}
