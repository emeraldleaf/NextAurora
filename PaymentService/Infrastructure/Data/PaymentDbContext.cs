using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;

namespace PaymentService.Infrastructure.Data;

/// <summary>
/// EF Core DbContext for PaymentService — backed by SQL Server. Same conventions as
/// <c>OrderDbContext</c> (string-stored enums, <c>RowVersion</c> concurrency tokens, money with
/// 18,2 precision). The notable extra constraint here is the unique index on <c>OrderId</c>:
/// at most one Payment per Order, enforced at the database level.
///
/// <para>
/// <b>Why a unique index on <c>OrderId</c>:</b> the saga can deliver <c>OrderPlacedEvent</c>
/// more than once (broker redeliveries, DLQ replays). The handler does an existence check
/// first, but races between two simultaneous deliveries could still attempt two inserts. The
/// unique index turns the second insert into a database error rather than two payments for one
/// order. Defense in depth.
/// </para>
/// </summary>
public class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

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

            // One Payment per Order — see class summary.
            entity.HasIndex(e => e.OrderId).IsUnique();

            entity.Property<byte[]>("RowVersion").IsRowVersion();
        });
    }
}
