using Microsoft.EntityFrameworkCore;
using ShippingService.Domain.Entities;
using ShippingService.Infrastructure.EventLog;

namespace ShippingService.Infrastructure.Data;

public class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<TrackingEvent> TrackingEvents => Set<TrackingEvent>();
    public DbSet<EventLogEntry> EventLogs => Set<EventLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shipment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Carrier).HasMaxLength(50);
            entity.Property(e => e.TrackingNumber).HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasMany(e => e.TrackingEvents).WithOne().HasForeignKey(t => t.ShipmentId);
            entity.HasIndex(e => e.OrderId);
        });

        modelBuilder.Entity<TrackingEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Status).HasMaxLength(20);
        });

        modelBuilder.Entity<EventLogEntry>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasMaxLength(256);
            entity.Property(e => e.Topic).HasMaxLength(256);
            entity.Property(e => e.Payload).HasColumnType("text");
            entity.Property(e => e.CorrelationId).HasMaxLength(256);
            entity.Property(e => e.EntityId).HasMaxLength(256);
            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => e.OccurredAt);
            entity.HasIndex(e => e.EventType);
            entity.HasIndex(e => e.EntityId);
        });
    }
}
