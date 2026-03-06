using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Entities;
using OrderService.Infrastructure.EventLog;

namespace OrderService.Infrastructure.Data;

public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<EventLogEntry> EventLogs => Set<EventLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TotalAmount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.OrderId);
            entity.Navigation(e => e.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.HasIndex(e => e.BuyerId);
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProductName).HasMaxLength(200);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
        });

        modelBuilder.Entity<EventLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasMaxLength(256);
            entity.Property(e => e.Topic).HasMaxLength(256);
            entity.Property(e => e.Payload).HasColumnType("nvarchar(max)");
            entity.Property(e => e.CorrelationId).HasMaxLength(256);
            entity.Property(e => e.EntityId).HasMaxLength(256);
            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.EntityId);
        });
    }
}
