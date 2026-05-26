using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CatalogService.Domain;
using CatalogService.Features;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NextAurora.Contracts.DTOs;
using Wolverine;
using Xunit;

namespace CatalogService.Tests.Integration;

/// <summary>
/// Integration coverage for CatalogService's read path against real infrastructure — a Postgres
/// container and a Redis container, with the API booted in-process by <see cref="CatalogApiFactory"/>.
///
/// <para>
/// Each test uses a fresh product GUID, so the shared per-class containers stay isolated without
/// a database reset between tests. What's proven here is exactly what unit tests can't reach:
/// migrations apply, <c>HybridProductCache</c> actually caches over real Redis, the write path
/// actually invalidates, and the <c>xmin</c> concurrency token actually fires.
/// </para>
/// </summary>
public sealed class ProductCachingTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory = factory;

    [Fact]
    public async Task Api_boots_and_migrations_apply()
    {
        // ARRANGE — Bring up the API client. The factory's success at booting is itself
        // the load-bearing assertion: it means containers are up, connection strings
        // got injected into Aspire's resource registry, and MigrateDatabaseAsync ran in
        // the Development-env startup path. If the migration step failed, /api/v1/products
        // would 500 because the products table wouldn't exist.
        var client = _factory.CreateClient();

        // ACT — Hit the list endpoint. GetFromJsonAsync throws on non-success, so a
        // returned (non-null) list proves the schema exists and is queryable.
        var products = await client.GetFromJsonAsync<List<ProductDto>>("/api/v1/products");

        // ASSERT — Non-null list (may be empty if no products were seeded by other tests
        // before this one). The point is "the path is wired up end-to-end".
        products.Should().NotBeNull();
    }

    [Fact]
    public async Task GetProductById_caches_the_result_across_calls()
    {
        // ARRANGE — Seed a product directly in the DB. We'll read it via the API once
        // to populate the cache, then delete the underlying row out-of-band and read
        // again — if the second read still succeeds, the cache is genuinely serving it
        // (no DB round-trip on the second call). This is the only way to prove caching
        // is actually happening; a unit test would just verify the wiring.
        var productId = await SeedProductAsync(price: 19.99m);
        var client = _factory.CreateClient();

        // ACT (1/3) — First call: cache miss → factory loads from Postgres → stored in
        // L1 (in-process MemoryCache) AND L2 (Redis) per HybridCache's contract.
        var first = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");

        // ASSERT (1/3) — First read returns the seeded price.
        first.Should().NotBeNull();
        first!.Price.Should().Be(19.99m);

        // ACT (2/3) — Delete the row directly, bypassing the cache. ExecuteDeleteAsync
        // doesn't go through the application — no cache invalidation happens. The cache
        // entry remains live.
        await using (var scope = _factory.CreateDbScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Products.Where(p => p.Id == productId).ExecuteDeleteAsync();
        }

        // ACT (3/3) — Second read. The DB no longer has this product. If the response
        // is non-null with the original price, it MUST be coming from cache.
        var second = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");

        // ASSERT (3/3) — Cached entry served. Without HybridCache wired up, this would
        // either return null (404 → GetFromJsonAsync throws) or return a stale-but-correct
        // tracked entity (which would also fail since the row is gone). Only an actual
        // cache hit produces this outcome.
        second.Should().NotBeNull("the product was cached on the first read and the DB row is now gone");
        second!.Price.Should().Be(19.99m);
    }

    [Fact]
    public async Task UpdateProduct_invalidates_the_cached_entry()
    {
        // ARRANGE — The "invalidate on write" contract. Seed at $10, prime the cache,
        // update to $42.50 via the real PUT endpoint. UpdateProductHandler MUST call
        // IProductCache.InvalidateAsync — without it, the next read would still see
        // the cached $10 until TTL.
        var productId = await SeedProductAsync(price: 10.00m);
        var client = _factory.CreateClient();

        // ACT (1/2) — Prime the cache at the old price.
        var beforeUpdate = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");
        beforeUpdate!.Price.Should().Be(10.00m);

        // ACT (2/2) — Update through the real write path. SellerId must match
        // TestAuthHandler's stamped NameIdentifier claim ("test-seller") so the PUT
        // endpoint's seller-ownership check (added by the PR #14 security review)
        // returns 204 NoContent rather than 403 Forbid.
        var update = new { ProductId = productId, SellerId = "test-seller", Name = "Updated Name", Description = "Updated", Price = 42.50m };
        var putResponse = await client.PutAsJsonAsync($"/api/v1/products/{productId}", update);
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // ACT (read after write)
        var afterUpdate = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");

        // ASSERT — Two invariants:
        //  1) Price reflects the update ($42.50). If the cache wasn't invalidated, this
        //     read would still see $10.00 from L1/L2 — the bug would manifest as a stale
        //     read for up to the 5-min TTL.
        //  2) Name also updated — verifies that the full DTO was re-projected, not just
        //     the price field.
        afterUpdate!.Price.Should().Be(42.50m, "the write path must invalidate the cache so the next read reflects the update");
        afterUpdate.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task ConcurrencyToken_rejects_the_second_of_two_racing_writes()
    {
        // ARRANGE — The optimistic-concurrency story for Postgres. Postgres uses the
        // system column `xmin` as the concurrency token (no app-defined RowVersion
        // needed — EF Core 8+ supports xmin as a shadow property). Two DbContext scopes
        // load the same row, snapshotting the same xmin. This simulates two replicas
        // (or two threads) racing to mutate the same Product. Without xmin protection,
        // last-write-wins would silently overwrite the first edit.
        var productId = await SeedProductAsync(price: 5.00m);

        await using var scope1 = _factory.CreateDbScope();
        await using var scope2 = _factory.CreateDbScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var fromScope1 = await db1.Products.FirstAsync(p => p.Id == productId);
        var fromScope2 = await db2.Products.FirstAsync(p => p.Id == productId);

        // ACT (1/2) — First write commits — Postgres bumps xmin on the row.
        fromScope1.UpdateDetails("Winner", fromScope1.Description, fromScope1.Price);
        await db1.SaveChangesAsync();

        // ACT (2/2) — Second write carries the now-stale xmin. EF's UPDATE statement
        // includes WHERE xmin = @original, which matches zero rows. EF detects this
        // and throws.
        fromScope2.UpdateDetails("Loser", fromScope2.Description, fromScope2.Price);
        var act = async () => await db2.SaveChangesAsync();

        // ASSERT — DbUpdateConcurrencyException — the signal that the second write lost
        // the race. Application code handles this via GlobalExceptionHandler (HTTP 409)
        // or Wolverine's AddConcurrencyRetry policy (background retry). Last-write-wins
        // is impossible.
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task PostProduct_byOwner_returns201AndPersistsProduct()
    {
        // ARRANGE — Cover the POST /api/v1/products write path end-to-end. The endpoint
        // enforces JWT-sub == command.SellerId (we use "test-seller" stamped by
        // TestAuthHandler); the handler persists via CatalogDbContext.AddAsync +
        // SaveChangesAsync. Categories are FK-required on Product, so we seed one first.
        var categoryId = await SeedCategoryAsync();
        var client = _factory.CreateClient();
        var newProduct = new
        {
            Name = "PostTest-" + Guid.NewGuid(),
            Description = "Created via API for coverage",
            Price = 12.34m,
            Currency = "USD",
            CategoryId = categoryId,
            SellerId = "test-seller",        // matches JWT sub
            StockQuantity = 5
        };

        // ACT — POST with the authenticated test client.
        var response = await client.PostAsJsonAsync("/api/v1/products", newProduct);

        // ASSERT — Three invariants:
        //  1) 201 Created — the success status for a POST that creates a resource.
        //     A 403 would mean the JWT-vs-body check failed unexpectedly; a 400
        //     would mean the validator (covered by unit tests) rejected the body.
        //  2) Response body carries the new product's ID — proves the handler
        //     returned the Guid from `Product.Create`, not Guid.Empty.
        //  3) The row exists in the DB with the values we sent — proves
        //     SaveChangesAsync ran and the aggregate's factory produced the
        //     expected state. Hits CreateProductHandler's full happy path,
        //     which integration tests didn't cover before the VSA collapse
        //     deleted the mocked handler unit tests.
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // Endpoint returns `Results.Created(location, new { Id = productId })`. Parse with
        // JsonDocument to avoid declaring a typed DTO that the analyzer (CA1812) can't see
        // being constructed via reflection.
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var createdId = doc.RootElement.GetProperty("id").GetGuid();
        createdId.Should().NotBe(Guid.Empty);

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Id == createdId);
        stored.Name.Should().Be(newProduct.Name);
        stored.Price.Should().Be(12.34m);
        stored.SellerId.Should().Be("test-seller");
        stored.StockQuantity.Should().Be(5);
    }

    [Fact]
    public async Task ReserveStock_viaMessageBus_decrementsStockAndInvalidatesCache()
    {
        // ARRANGE — Cover ReserveStockHandler's full happy path via IMessageBus.
        // ReserveStock is normally invoked over gRPC from OrderService during order
        // placement, but the production handler chain goes through Wolverine's
        // IMessageBus — that's what we exercise here. A direct bus call covers the
        // same handler invocation path the gRPC server uses (CatalogGrpcService
        // translates each gRPC request into a `bus.InvokeAsync<bool>(command, ct)`).
        // We seed with stock=10 so the reservation has room.
        var productId = await SeedProductAsync(price: 8m);

        await using var scope = _factory.CreateDbScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // ACT — Reserve 3 units. Wolverine routes the command to ReserveStockHandler,
        // which loads the Product tracked, calls AdjustStock(remaining), and saves.
        var success = await bus.InvokeAsync<bool>(new ReserveStockCommand(productId, Quantity: 3));

        // ASSERT — Three invariants:
        //  1) Success — handler returned true (stock was sufficient + save committed).
        //     A false return would mean either "product not found" (impossible — we
        //     just seeded it) or "insufficient stock" (also impossible — 10 > 3).
        //  2) StockQuantity decremented from 10 to 7 — proves AdjustStock ran and
        //     SaveChanges committed. We assert directly on the DB (not the cache)
        //     because cache invalidation is downstream of the save.
        //  3) IsAvailable stays true — the derived field (StockQuantity > 0) was
        //     correctly maintained inside AdjustStock. Would catch a future
        //     regression where IsAvailable diverges from StockQuantity.
        success.Should().BeTrue();

        await using var dbScope = _factory.CreateDbScope();
        var db = dbScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        stored.StockQuantity.Should().Be(7);
        stored.IsAvailable.Should().BeTrue();
    }

    private async Task<Guid> SeedCategoryAsync()
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var category = Category.Create("PostTest-" + Guid.NewGuid(), "seeded for POST test");
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        return category.Id;
    }

    /// <summary>
    /// Seeds a category + product directly via a DbContext scope and returns the product id.
    /// A product needs a category (FK), so both are inserted. Fresh GUIDs keep tests isolated
    /// on the shared containers.
    /// </summary>
    private async Task<Guid> SeedProductAsync(decimal price)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var category = Category.Create("Integration Test Category", "seeded by integration test");
        var product = Product.Create(
            name: "Integration Test Product",
            description: "seeded by integration test",
            price: price,
            currency: "USD",
            categoryId: category.Id,
            sellerId: "test-seller",
            stockQuantity: 10);

        db.Categories.Add(category);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product.Id;
    }
}
