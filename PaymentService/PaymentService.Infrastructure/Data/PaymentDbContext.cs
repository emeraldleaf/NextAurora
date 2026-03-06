using Microsoft.EntityFrameworkCore;
using PaymentService.Domain.Entities;
using PaymentService.Infrastructure.EventLog;

namespace PaymentService.Infrastructure.Data;

public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<EventLogEntry> EventLogs => Set<EventLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Provider).HasMaxLength(50);
            entity.Property(e => e.ExternalTransactionId).HasMaxLength(200);
            entity.Property(e => e.FailureReason).HasMaxLength(500);
            entity.HasIndex(e => e.OrderId);
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Reason).HasMaxLength(500);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
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
