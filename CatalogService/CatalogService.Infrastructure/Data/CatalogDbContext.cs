using CatalogService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Data;

/// <summary>
/// EF Core DbContext for CatalogService — backed by PostgreSQL. Defines the schema mapping for
/// <see cref="Product"/> and <see cref="Category"/> aggregates: column lengths, precision,
/// indexes, relationships, and the optimistic-concurrency token strategy.
///
/// <para>
/// <b>Postgres concurrency token (<c>xmin</c>):</b> every Postgres row carries a system column
/// called <c>xmin</c> — the transaction ID that last wrote the row. We register it as a shadow
/// property mapped to the <c>xid</c> column type and configure it as EF's concurrency token.
/// EF then includes <c>WHERE xmin = @originalXmin</c> on every UPDATE; if another transaction
/// touched the row first, the WHERE matches zero rows and EF throws
/// <see cref="DbUpdateConcurrencyException"/>. The handler layer catches that and either retries
/// (Service Bus events) or returns 409 Conflict (HTTP) — see <c>GlobalExceptionHandler</c> and
/// <c>AddConcurrencyRetry</c>. Net result: last-write-wins is impossible.
/// </para>
/// <para>
/// <b>Why a shadow property and not a real column:</b> <c>xmin</c> already exists on every row;
/// we don't need to add a column. The shadow property is just EF's way of binding to that
/// existing system column. The convenience method <c>UseXminAsConcurrencyToken()</c> existed in
/// older Npgsql versions but was removed in Npgsql 9+; the manual form below is canonical.
/// </para>
/// </summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id);

            // String length caps: enforced both server-side (validators) and at the DB layer
            // so a misbehaving client can't write a 1MB description and inflate the table.
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(2000);

            // Money columns get explicit precision: 18 digits total, 2 after the decimal.
            // Without this EF would default to a less precise mapping and silently lose cents
            // on values like 1234567890.99.
            entity.Property(e => e.Price).HasPrecision(18, 2);
            entity.Property(e => e.Currency).HasMaxLength(3);
            entity.Property(e => e.SellerId).HasMaxLength(100);

            // Many products per category, expressed in EF's relationship config. Foreign-key
            // type is implied from the property type (Guid).
            entity.HasOne(e => e.Category).WithMany(c => c.Products).HasForeignKey(e => e.CategoryId);

            // Indexes on filter/lookup columns — the catalog endpoint paginates by Id, but
            // these support `GetByCategoryAsync` and seller-scoped queries without sequential
            // scans. Composite indexes can serve multiple queries at once; single-column is
            // fine here because each lookup is one-dimensional.
            entity.HasIndex(e => e.CategoryId);
            entity.HasIndex(e => e.SellerId);

            // Postgres `xmin` concurrency token — see class summary.
            entity.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        });
    }
}
