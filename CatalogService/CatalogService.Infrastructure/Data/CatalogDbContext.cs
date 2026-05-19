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

        SeedDemoData(modelBuilder);
    }

    /// <summary>
    /// Declarative seed data, materialized as a migration via EF Core's <c>HasData()</c>. Generated
    /// once via <c>dotnet ef migrations add SeedDemoCatalog</c>; on every subsequent boot the
    /// migration is a no-op (already in <c>__EFMigrationsHistory</c>). Used for demo deployments
    /// so the API returns non-empty responses out of the box.
    ///
    /// <para>
    /// GUIDs and the seed seller ID are fixed (not <c>Guid.NewGuid()</c>) so the migration is
    /// deterministic — re-running the model snapshot wouldn't emit a diff. The fixed
    /// <c>CreatedAt</c> is similarly required: a dynamic value would re-generate a different
    /// migration every time.
    /// </para>
    /// <para>
    /// <c>HasData</c> bypasses the entity's factory method (<c>Product.Create</c>) and its private
    /// setters — EF Core writes via reflection. That's fine here because the seed data is curated
    /// at design time, not derived from runtime input that needs validation. We still set
    /// <c>IsAvailable</c> explicitly to match the <c>StockQuantity &gt; 0</c> invariant the
    /// factory would have enforced.
    /// </para>
    /// </summary>
    private static void SeedDemoData(ModelBuilder modelBuilder)
    {
        var electronicsId = new Guid("11111111-1111-1111-1111-111111111111");
        var homeId = new Guid("22222222-2222-2222-2222-222222222222");
        var booksId = new Guid("33333333-3333-3333-3333-333333333333");

        modelBuilder.Entity<Category>().HasData(
            new { Id = electronicsId, Name = "Electronics", Description = "Phones, laptops, headphones, accessories" },
            new { Id = homeId, Name = "Home & Kitchen", Description = "Furniture, decor, appliances" },
            new { Id = booksId, Name = "Books", Description = "Fiction, non-fiction, technical" });

        // SellerId is a fixed seed value — in real usage this would be a Keycloak `sub` claim.
        const string SeedSellerId = "seed-seller-1";
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        modelBuilder.Entity<Product>().HasData(
            new { Id = new Guid("a0000001-0000-0000-0000-000000000001"), Name = "NextAurora Laptop 15\"",      Description = "Demo laptop for the NextAurora portfolio system. Lightweight, fast, fictional.", Price = 1299.00m, Currency = "USD", CategoryId = electronicsId, SellerId = SeedSellerId, StockQuantity = 12, IsAvailable = true, CreatedAt = createdAt },
            new { Id = new Guid("a0000001-0000-0000-0000-000000000002"), Name = "Wireless Headphones Pro",    Description = "Bluetooth 5.3, 30-hour battery, ANC.",                                              Price = 199.00m,  Currency = "USD", CategoryId = electronicsId, SellerId = SeedSellerId, StockQuantity = 45, IsAvailable = true, CreatedAt = createdAt },
            new { Id = new Guid("a0000001-0000-0000-0000-000000000003"), Name = "USB-C Hub (8-in-1)",         Description = "HDMI 4K, 100W PD, 2x USB-A 3.0, SD/microSD, Ethernet.",                             Price = 49.00m,   Currency = "USD", CategoryId = electronicsId, SellerId = SeedSellerId, StockQuantity = 0,  IsAvailable = false, CreatedAt = createdAt },
            new { Id = new Guid("a0000002-0000-0000-0000-000000000001"), Name = "Standing Desk (Electric)",   Description = "48\" wide, dual motor, memory presets. Stable up to 250lb.",                        Price = 599.00m,  Currency = "USD", CategoryId = homeId,        SellerId = SeedSellerId, StockQuantity = 8,  IsAvailable = true, CreatedAt = createdAt },
            new { Id = new Guid("a0000002-0000-0000-0000-000000000002"), Name = "Ceramic Pour-Over Kettle",   Description = "1L gooseneck, induction-compatible.",                                                Price = 79.00m,   Currency = "USD", CategoryId = homeId,        SellerId = SeedSellerId, StockQuantity = 22, IsAvailable = true, CreatedAt = createdAt },
            new { Id = new Guid("a0000003-0000-0000-0000-000000000001"), Name = "Designing Data-Intensive Applications", Description = "Martin Kleppmann. The standard reference for distributed systems.",       Price = 45.00m,   Currency = "USD", CategoryId = booksId,       SellerId = SeedSellerId, StockQuantity = 30, IsAvailable = true, CreatedAt = createdAt },
            new { Id = new Guid("a0000003-0000-0000-0000-000000000002"), Name = "The Pragmatic Programmer (20th Anniversary)", Description = "Hunt & Thomas. Updated edition.",                                    Price = 38.00m,   Currency = "USD", CategoryId = booksId,       SellerId = SeedSellerId, StockQuantity = 15, IsAvailable = true, CreatedAt = createdAt });
    }
}
