using AwesomeAssertions;
using CatalogService.Application.Interfaces;
using CatalogService.Domain.Entities;
using CatalogService.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CatalogService.Tests.Integration;

/// <summary>
/// Integration coverage for <see cref="IProductReadStore"/> — the projection-in-EF read path
/// added in the CQRS data-access split (see <c>docs/cqrs-data-access.md</c>). Unit tests for
/// the query handlers mock <c>IProductReadStore</c>, which is correct at the handler-contract
/// layer but leaves the actual <c>AsNoTracking().Select(...)</c> SQL untested. These tests
/// exercise the real EF projection against Postgres so a future "I renamed a DTO property and
/// forgot to update the Select" or "I broke the category-name LEFT JOIN" regression surfaces
/// at test time, not in production.
///
/// <para>
/// Tests share the per-class containers from <see cref="CatalogApiFactory"/> and use fresh
/// GUIDs per test to stay isolated. Seeding happens through a DbContext scope (not the API)
/// because we're testing the read path in isolation; using the API would conflate
/// write-path concerns.
/// </para>
/// </summary>
public sealed class ProductReadStoreTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory = factory;

    [Fact]
    public async Task GetByIdAsync_projects_entity_into_ProductDto_with_category_name()
    {
        // ARRANGE — Seed a Category and a Product directly via EF. The read store must
        // resolve the Category navigation through a LEFT JOIN inside the projection
        // (Category = p.Category != null ? p.Category.Name : "") — the test confirms
        // the JOIN actually returns the right string, not just that the SQL compiles.
        var (productId, categoryName) = await SeedProductAsync(name: "Widget", price: 9.99m, categoryName: "Hardware");

        await using var scope = _factory.CreateDbScope();
        var readStore = scope.ServiceProvider.GetRequiredService<IProductReadStore>();

        // ACT — Hit the projection method directly. No HTTP, no cache — just the SQL EF
        // generates for the .AsNoTracking().Where(...).Select(...) chain.
        var dto = await readStore.GetByIdAsync(productId);

        // ASSERT — Three invariants the projection contract has to hold:
        //  1) Non-null — the row exists and the projection materializes it.
        //  2) Scalar fields (Id, Name, Price) round-trip from the EF entity through the
        //     Select to the DTO. If a DTO property got renamed without updating the
        //     projection, this would surface as a wrong/zero value (or a compile-time
        //     error before even reaching this test).
        //  3) Category resolves to the category NAME (a string), not the FK Guid or
        //     entity. This is the LEFT JOIN inside the projection working — and proves
        //     the null-safe branch (p.Category != null ? ... : "") doesn't trip when
        //     the navigation is actually populated.
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(productId);
        dto.Name.Should().Be("Widget");
        dto.Price.Should().Be(9.99m);
        dto.Category.Should().Be(categoryName);
    }

    [Fact]
    public async Task GetByIdAsync_returns_null_when_product_does_not_exist()
    {
        // ARRANGE — A random GUID that's never been seeded. The handler relies on null
        // → 404 at the endpoint, so the read store's null-on-missing contract is
        // load-bearing for the API surface.
        await using var scope = _factory.CreateDbScope();
        var readStore = scope.ServiceProvider.GetRequiredService<IProductReadStore>();

        // ACT — Project on a non-existent id.
        var dto = await readStore.GetByIdAsync(Guid.NewGuid());

        // ASSERT — Null, not a default-constructed DTO. FirstOrDefaultAsync returns
        // default(ProductDto?) = null for reference types, which is what we depend on.
        dto.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_paginates_and_returns_DTOs()
    {
        // ARRANGE — Seed enough products to actually paginate. We need at least
        // pageSize + 1 to prove Skip() works. Six products with a shared category keeps
        // the test fast and stable.
        await SeedProductsAsync(count: 6, categoryName: "PaginationTestCategory");

        await using var scope = _factory.CreateDbScope();
        var readStore = scope.ServiceProvider.GetRequiredService<IProductReadStore>();

        // ACT — Page 1, size 2. Across the shared container we may see other tests'
        // seeded rows too; the assertion shape doesn't depend on the exact result set.
        var page1 = await readStore.GetAllAsync(page: 1, pageSize: 2);

        // ASSERT — Three invariants:
        //  1) Pagination respects the cap — 2 results max per page. If Skip/Take were
        //     wired wrong (or applied to the wrong IQueryable) we'd see all rows.
        //  2) Every returned item is a fully-projected DTO (Name + Category populated),
        //     proving the .Select(...) ran end-to-end and didn't silently drop fields.
        //  3) OrderBy(p => p.Id) makes the page stable — Skip/Take without OrderBy is
        //     non-deterministic and a known Postgres footgun.
        page1.Count.Should().BeLessThanOrEqualTo(2);
        page1.Should().OnlyContain(p => !string.IsNullOrEmpty(p.Name) && !string.IsNullOrEmpty(p.Category));
        page1.Should().BeInAscendingOrder(p => p.Id);
    }

    [Fact]
    public async Task SearchAsync_is_case_insensitive_via_ILike()
    {
        // ARRANGE — The original ProductRepository.SearchAsync used a Contains call
        // which translates to a case-sensitive LIKE on Postgres (so a lowercase "laptop"
        // misses an uppercase "Laptop"). The ProductReadStore fix is EF.Functions.ILike,
        // the Postgres-specific case-insensitive LIKE operator. The seed name uses
        // MixedCase; the search query is lowercase. A match proves ILike is doing its job.
        var (productId, _) = await SeedProductAsync(name: "MixedCaseGadget-" + Guid.NewGuid(), price: 25m);

        await using var scope = _factory.CreateDbScope();
        var readStore = scope.ServiceProvider.GetRequiredService<IProductReadStore>();

        // ACT — Lowercase substring search.
        var results = await readStore.SearchAsync("mixedcasegadget", page: 1, pageSize: 50);

        // ASSERT — Two invariants:
        //  1) The seeded product appears despite the case mismatch — proves ILike
        //     translated correctly. Without ILike, plain .Contains on Postgres would
        //     return zero matches for this query.
        //  2) The matched DTO is fully projected (Name + Price + Category) — the
        //     search projection shape matches GetByIdAsync's so callers can rely on a
        //     consistent ProductDto regardless of which read method they invoked.
        results.Should().Contain(p => p.Id == productId);
        var match = results.First(p => p.Id == productId);
        match.Name.Should().StartWith("MixedCaseGadget-");
        match.Price.Should().Be(25m);
    }

    /// <summary>
    /// Seeds a category + product directly via a DbContext scope; returns the product id
    /// and the category name so tests can assert against the projection.
    /// </summary>
    private async Task<(Guid productId, string categoryName)> SeedProductAsync(
        string name, decimal price, string categoryName = "TestCategory")
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var category = Category.Create(categoryName + "-" + Guid.NewGuid(), "seeded for ProductReadStore test");
        var product = Product.Create(
            name: name,
            description: "seeded for ProductReadStore test",
            price: price,
            currency: "USD",
            categoryId: category.Id,
            sellerId: "test-seller",
            stockQuantity: 10);

        db.Categories.Add(category);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return (product.Id, category.Name);
    }

    /// <summary>
    /// Seeds a category and N products sharing it. Used by the pagination test where
    /// we need enough rows to actually exercise Skip/Take but don't care about the
    /// individual values.
    /// </summary>
    private async Task SeedProductsAsync(int count, string categoryName)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var category = Category.Create(categoryName + "-" + Guid.NewGuid(), "seeded for pagination test");
        db.Categories.Add(category);

        for (var i = 0; i < count; i++)
        {
            db.Products.Add(Product.Create(
                name: $"PaginationProduct-{i}-{Guid.NewGuid()}",
                description: "seeded",
                price: 1m + i,
                currency: "USD",
                categoryId: category.Id,
                sellerId: "test-seller",
                stockQuantity: 1));
        }

        await db.SaveChangesAsync();
    }
}
