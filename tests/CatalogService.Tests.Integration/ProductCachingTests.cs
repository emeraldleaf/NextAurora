using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using CatalogService.Domain.Entities;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NextAurora.Contracts.DTOs;
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
        // The factory booting at all means: containers up, connection strings injected,
        // and MigrateDatabaseAsync ran in the Development-env startup path. GetFromJsonAsync
        // throws on a non-success status, so a returned (non-null) list proves the schema
        // exists and is queryable.
        var client = _factory.CreateClient();

        var products = await client.GetFromJsonAsync<List<ProductDto>>("/api/v1/products");

        products.Should().NotBeNull();
    }

    [Fact]
    public async Task GetProductById_caches_the_result_across_calls()
    {
        var productId = await SeedProductAsync(price: 19.99m);
        var client = _factory.CreateClient();

        // First call: cache miss → factory loads from Postgres → stored in L1 + L2.
        var first = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");
        first.Should().NotBeNull();
        first!.Price.Should().Be(19.99m);

        // Delete the row directly, bypassing the cache. If the next read still succeeds,
        // it was served from cache — the DB no longer has this product.
        await using (var scope = _factory.CreateDbScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await db.Products.Where(p => p.Id == productId).ExecuteDeleteAsync();
        }

        var second = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");
        second.Should().NotBeNull("the product was cached on the first read and the DB row is now gone");
        second!.Price.Should().Be(19.99m);
    }

    [Fact]
    public async Task UpdateProduct_invalidates_the_cached_entry()
    {
        var productId = await SeedProductAsync(price: 10.00m);
        var client = _factory.CreateClient();

        // Prime the cache at the old price.
        var beforeUpdate = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");
        beforeUpdate!.Price.Should().Be(10.00m);

        // Update through the real write path — UpdateProductHandler must call
        // IProductCache.InvalidateAsync, or the next read would still see 10.00.
        // SellerId must match TestAuthHandler's stamped NameIdentifier claim ("test-seller")
        // so the PUT endpoint's seller-ownership check (added by the PR #14 security review)
        // returns 204 NoContent rather than 403 Forbid.
        var update = new { ProductId = productId, SellerId = "test-seller", Name = "Updated Name", Description = "Updated", Price = 42.50m };
        var putResponse = await client.PutAsJsonAsync($"/api/v1/products/{productId}", update);
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterUpdate = await client.GetFromJsonAsync<ProductDto>($"/api/v1/products/{productId}");
        afterUpdate!.Price.Should().Be(42.50m, "the write path must invalidate the cache so the next read reflects the update");
        afterUpdate.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task ConcurrencyToken_rejects_the_second_of_two_racing_writes()
    {
        var productId = await SeedProductAsync(price: 5.00m);

        // Two independent DbContext scopes load the same row — same xmin value snapshotted
        // into each tracked entity.
        await using var scope1 = _factory.CreateDbScope();
        await using var scope2 = _factory.CreateDbScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var fromScope1 = await db1.Products.FirstAsync(p => p.Id == productId);
        var fromScope2 = await db2.Products.FirstAsync(p => p.Id == productId);

        // First write commits — Postgres bumps xmin on the row.
        fromScope1.UpdateDetails("Winner", fromScope1.Description, fromScope1.Price);
        await db1.SaveChangesAsync();

        // Second write carries the now-stale xmin. EF's UPDATE ... WHERE xmin = @original
        // matches zero rows → DbUpdateConcurrencyException. Last-write-wins is impossible.
        fromScope2.UpdateDetails("Loser", fromScope2.Description, fromScope2.Price);
        var act = async () => await db2.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
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
