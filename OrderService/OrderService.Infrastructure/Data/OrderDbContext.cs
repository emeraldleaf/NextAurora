using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Entities;

namespace OrderService.Infrastructure.Data;

/// <summary>
/// EF Core DbContext for OrderService — backed by SQL Server. Maps the <see cref="Order"/>
/// aggregate (with its <see cref="OrderLine"/> children) to the database.
///
/// <para>
/// <b>SQL Server concurrency token (<c>RowVersion</c>):</b> SQL Server has a built-in
/// <c>rowversion</c> type — an 8-byte counter the engine increments on every row change. We
/// declare it as a shadow property so the entity class stays clean of infrastructure concerns;
/// EF generates a real column behind the scenes (see the migration). Same effect as Postgres
/// <c>xmin</c> but realized differently per provider.
/// </para>
/// <para>
/// <b>Status as string:</b> <c>OrderStatus</c> is persisted as its name (<c>"Placed"</c>,
/// <c>"Paid"</c>, etc.) rather than the underlying int. If we ever reorder or rename enum
/// members, old rows still make sense. Trade-off: 20 bytes per row vs 4. For our scale, the
/// readability and safety win easily.
/// </para>
/// <para>
/// <b>Backing-field navigation:</b> <c>UsePropertyAccessMode(PropertyAccessMode.Field)</c> tells
/// EF to write into <c>Order._lines</c> directly when materializing children, bypassing the
/// public read-only <c>Lines</c> property. Without this, EF would try to mutate the read-only
/// list and fail. This is the standard pattern for properly-encapsulated aggregates.
/// </para>
/// </summary>
public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TotalAmount).HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasMaxLength(3);

            // OrderStatus enum stored as its string name for forward compatibility.
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

            // One-to-many to OrderLine. The unidirectional relationship (no inverse navigation
            // on OrderLine) reflects that lines are children of the aggregate — they don't have
            // an independent "navigate to my Order" use case.
            entity.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.OrderId);

            // See class summary: write through the private backing field, not the public property.
            entity.Navigation(e => e.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

            // BuyerId is the lookup column for the buyer's order history endpoint.
            entity.HasIndex(e => e.BuyerId);

            // SQL Server `rowversion` concurrency token — see class summary.
            entity.Property<byte[]>("RowVersion").IsRowVersion();
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProductName).HasMaxLength(200);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            // No concurrency token on lines: they're never mutated independently. The parent
            // Order's RowVersion guards the whole aggregate.
        });
    }
}
