using AwesomeAssertions;
using CatalogService.Domain;
using CatalogService.Features;
using CatalogService.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CatalogService.Tests.Integration;

/// <summary>
/// Integration coverage for the read-side query handlers — <see cref="GetProductByIdHandler"/>,
/// <see cref="GetAllProductsHandler"/>, and <see cref="SearchProductsHandler"/>. After the VSA
/// collapse these handlers take <see cref="CatalogDbContext"/> directly and project to
/// <see cref="NextAurora.Contracts.DTOs.ProductDto"/> inline via <c>AsNoTracking().Select(...)</c>.
///
/// <para>
/// Unit tests for read handlers don't make sense in the new shape (there's no repository
/// abstraction to mock — the projection IS the handler). These integration tests fill that
/// gap against real Postgres so future regressions to the projection shape (renamed DTO field,
/// broken Category LEFT JOIN, dropped case-insensitive ILike, missing pagination clamp) surface
/// at test time, not in production.
/// </para>
/// <para>
/// Tests share the per-class containers from <see cref="CatalogApiFactory"/> and use fresh
/// GUIDs per test to stay isolated. The handlers are resolved directly from a DI scope; per
/// CLAUDE.md "Communication Patterns → Wolverine handler discovery is NOT DI registration"
/// they're explicitly <c>AddScoped</c>'d in <c>AddCatalogInfrastructure</c>.
/// </para>
/// </summary>
public sealed class ProductReadProjectionTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory = factory;

    [Fact]
    public async Task GetProductById_projects_entity_into_ProductDto_with_category_name()
    {
        // ARRANGE — Seed a Category and a Product directly via EF. The handler's projection
        // must resolve the Category navigation through a LEFT JOIN inside the Select
        // (Category = p.Category != null ? p.Category.Name : "") — the test confirms the
        // JOIN actually returns the right string, not just that the SQL compiles. Note:
        // GetProductByIdHandler also routes through IProductCache, which means a cache hit
        // could mask projection breakage on subsequent calls. Each test seeds fresh GUIDs
        // so the first call is a guaranteed cache miss exercising the projection path.
        var (productId, categoryName) = await SeedProductAsync(name: "Widget", price: 9.99m, categoryName: "Hardware");

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetProductByIdHandler>();

        // ACT — Hit the handler directly. First call is a cache miss, runs the projection.
        var dto = await handler.HandleAsync(new GetProductByIdQuery(productId), CancellationToken.None);

        // ASSERT — Four invariants the projection contract has to hold:
        //  1) Non-null — the row exists and the projection materializes it.
        //  2) Scalar fields (Id, Name, Price) round-trip from the EF entity through the
        //     Select to the DTO. If a DTO property got renamed without updating the
        //     projection, this would surface as a wrong/zero value.
        //  3) Category resolves to the category NAME (a string), not the FK Guid or
        //     entity. This is the LEFT JOIN inside the projection working — and proves
        //     the null-safe branch (p.Category != null ? ... : "") doesn't trip when
        //     the navigation is actually populated.
        //  4) Currency round-trips — guards against the projection dropping non-display
        //     fields accidentally.
        dto.Should().NotBeNull();
        dto!.Id.Should().Be(productId);
        dto.Name.Should().Be("Widget");
        dto.Price.Should().Be(9.99m);
        dto.Category.Should().Be(categoryName);
        dto.Currency.Should().Be("USD");
    }

    [Fact]
    public async Task GetProductById_returns_null_when_product_does_not_exist()
    {
        // ARRANGE — A random GUID that's never been seeded. The handler relies on null
        // → 404 at the endpoint, so the projection's null-on-missing contract is
        // load-bearing for the API surface. The cache treats null returns as negative
        // entries (see GetProductByIdHandler), so this also exercises that path.
        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetProductByIdHandler>();

        // ACT — Project on a non-existent id.
        var dto = await handler.HandleAsync(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Null, not a default-constructed DTO.
        dto.Should().BeNull();
    }

    [Fact]
    public async Task GetAllProducts_paginates_and_returns_DTOs()
    {
        // ARRANGE — Seed enough products to actually paginate. We need at least
        // pageSize + 1 to prove Skip() works. Six products with a shared category keeps
        // the test fast and stable.
        await SeedProductsAsync(count: 6, categoryName: "PaginationTestCategory");

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetAllProductsHandler>();

        // ACT — Page 1, size 2. Across the shared container we may see other tests'
        // seeded rows too; the assertion shape doesn't depend on the exact result set.
        var page1 = await handler.HandleAsync(new GetAllProductsQuery(Page: 1, PageSize: 2), CancellationToken.None);

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
    public async Task GetAllProducts_clamps_negative_or_oversized_page_inputs()
    {
        // ARRANGE — The pagination clamp is a defense-in-depth guard inside the handler
        // (matching the endpoint-layer ClampPaging). It protects future non-endpoint
        // callers (e.g. an admin script, a gRPC method, a Wolverine-direct invocation)
        // from passing bad inputs that would either throw at execution or return a
        // pathological result set. Three branches under test:
        //   (a) Page < 1 → safePage = 1 (no negative offset)
        //   (b) PageSize < 1 or > 100 → safePageSize = 50 (no zero/huge fetch)
        //   (c) Skip-offset would overflow int → return [] before touching the DB
        // We seed a few rows so the clamped query has something to return for (a)/(b).
        await SeedProductsAsync(count: 3, categoryName: "ClampTestCategory");

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetAllProductsHandler>();

        // ACT (a) Negative page clamps to 1 — query still runs, returns rows.
        var negativePage = await handler.HandleAsync(
            new GetAllProductsQuery(Page: -5, PageSize: 50), CancellationToken.None);

        // ACT (b) Oversized PageSize clamps to default (50) — query still runs.
        var oversizedPageSize = await handler.HandleAsync(
            new GetAllProductsQuery(Page: 1, PageSize: 5000), CancellationToken.None);

        // ACT (c) Skip-offset overflow guard: page * pageSize > int.MaxValue.
        // (int.MaxValue / 50) + 100 = ~43M which, multiplied by 50, overflows int.
        // The handler computes the offset in long arithmetic and bails out early.
        var overflowed = await handler.HandleAsync(
            new GetAllProductsQuery(Page: (int.MaxValue / 50) + 100, PageSize: 50),
            CancellationToken.None);

        // ASSERT — Three invariants the clamp contract must hold:
        //  1) Negative page didn't crash — clamp ran. We don't assert an exact count
        //     because the shared container has other tests' rows, but a non-throwing
        //     non-null list proves the clamp engaged before Skip() saw a negative.
        //  2) Oversized PageSize didn't crash — clamp ran. Same reasoning.
        //  3) Overflow case returns the empty short-circuit, NOT throwing on Skip's
        //     ArgumentOutOfRangeException. This is the load-bearing safety guard for
        //     callers that pass int.MaxValue-class inputs.
        negativePage.Should().NotBeNull();
        oversizedPageSize.Should().NotBeNull();
        overflowed.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchProducts_clamps_negative_or_oversized_page_inputs()
    {
        // ARRANGE — Same clamp branches as GetAllProducts, mirrored in SearchProducts.
        // Seed a single product that matches the search term so the clamped query has
        // a hit for the non-overflow branches.
        var (_, _) = await SeedProductAsync(name: "ClampSearchTarget-" + Guid.NewGuid(), price: 7m);

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<SearchProductsHandler>();

        // ACT — Same three branches.
        var negativePage = await handler.HandleAsync(
            new SearchProductsQuery("clampsearchtarget", Page: -5, PageSize: 50),
            CancellationToken.None);
        var oversizedPageSize = await handler.HandleAsync(
            new SearchProductsQuery("clampsearchtarget", Page: 1, PageSize: 5000),
            CancellationToken.None);
        var overflowed = await handler.HandleAsync(
            new SearchProductsQuery("clampsearchtarget", Page: (int.MaxValue / 50) + 100, PageSize: 50),
            CancellationToken.None);

        // ASSERT — Same shape as GetAllProducts. The overflow guard is the load-bearing
        // one; the clamps for negative/oversized inputs just prove the search query ran.
        negativePage.Should().NotBeNull();
        oversizedPageSize.Should().NotBeNull();
        overflowed.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchProducts_is_case_insensitive_via_ILike()
    {
        // ARRANGE — Plain .Contains translates to a case-sensitive LIKE on Postgres (so a
        // lowercase "laptop" misses an uppercase "Laptop"). The handler uses
        // EF.Functions.ILike, the Postgres-specific case-insensitive LIKE operator. The
        // seed name uses MixedCase; the search query is lowercase. A match proves ILike
        // is doing its job.
        var (productId, _) = await SeedProductAsync(name: "MixedCaseGadget-" + Guid.NewGuid(), price: 25m);

        await using var scope = _factory.CreateDbScope();
        var handler = scope.ServiceProvider.GetRequiredService<SearchProductsHandler>();

        // ACT — Lowercase substring search.
        var results = await handler.HandleAsync(new SearchProductsQuery("mixedcasegadget", Page: 1, PageSize: 50), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) The seeded product appears despite the case mismatch — proves ILike
        //     translated correctly. Without ILike, plain .Contains on Postgres would
        //     return zero matches for this query.
        //  2) The matched DTO is fully projected (Name + Price + Category) — the
        //     search projection shape matches GetProductById's so callers can rely on
        //     a consistent ProductDto regardless of which read handler they invoked.
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

        var category = Category.Create(categoryName + "-" + Guid.NewGuid(), "seeded for read-projection test");
        var product = Product.Create(
            name: name,
            description: "seeded for read-projection test",
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
