using Microsoft.EntityFrameworkCore;
using ShippingService.Domain;

namespace ShippingService.Infrastructure.Data;

/// <summary>
/// EF Core DbContext for ShippingService — backed by PostgreSQL. Mirrors the patterns used in
/// the other DbContexts: enums as strings, money/identifier length caps, the Postgres-native
/// <c>xmin</c> concurrency token, and a unique index on <c>OrderId</c> so each order gets at
/// most one shipment.
/// </summary>
public class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<TrackingEvent> TrackingEvents => Set<TrackingEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Shipment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Carrier).HasMaxLength(50);
            entity.Property(e => e.TrackingNumber).HasMaxLength(50);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            // One-to-many: TrackingEvents belong to a Shipment. Unidirectional like Order/Lines.
            entity.HasMany(e => e.TrackingEvents).WithOne().HasForeignKey(t => t.ShipmentId);

            // One shipment per order. Like Payment, this is the database backstop against the
            // saga creating duplicate shipments under message redelivery.
            entity.HasIndex(e => e.OrderId).IsUnique();

            // Postgres `xmin` concurrency token.
            entity.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<TrackingEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Status).HasMaxLength(20);
            // No concurrency token: tracking events are append-only audit records, never updated.
        });
    }
}
