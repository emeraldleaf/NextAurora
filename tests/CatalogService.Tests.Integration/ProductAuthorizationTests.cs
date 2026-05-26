using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using CatalogService.Domain;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CatalogService.Tests.Integration;

/// <summary>
/// IDOR-prevention coverage for the PUT /api/v1/products/{id} endpoint — the seller-scope
/// variant of the canonical anti-enumeration pattern (see CLAUDE.md "Security Requirements"
/// and docs/cqrs-data-access.md). Per CLAUDE.md "Testing", every scoped-entity endpoint
/// requires an integration test asserting that a caller cannot mutate someone else's
/// resource and cannot distinguish "exists but not yours" from "does not exist."
///
/// <para>
/// <b>The attack model:</b> an authenticated seller (JWT subject "test-seller", stamped by
/// <see cref="TestAuthHandler"/>) submits a PUT against another seller's product. The body
/// carries the attacker's own SellerId so the endpoint's JWT-vs-body check passes; the
/// resource ownership mismatch is caught at the handler. The endpoint must respond with
/// 404 (not 403) so the attacker cannot learn whether the target product exists. The
/// stored row must be unchanged after the attempt.
/// </para>
/// </summary>
public sealed class ProductAuthorizationTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory = factory;

    [Fact]
    public async Task PutProduct_byNonOwner_returns404AndLeavesRowUnchanged()
    {
        // ARRANGE — Seed a product owned by a different seller (NOT "test-seller", the
        // JWT-authenticated principal). The attacker will PUT against this product's id,
        // submitting their own SellerId in the body so the endpoint's JWT-vs-body check
        // passes — only the handler's stored-vs-command ownership check stands between
        // them and a successful overwrite.
        var ownerSellerId = "owner-seller-" + Guid.NewGuid();
        var (productId, originalName, originalPrice) = await SeedProductAsync(
            sellerId: ownerSellerId, name: "Original Name", price: 100m);

        var client = _factory.CreateClient();
        var attack = new
        {
            ProductId = productId,
            SellerId = "test-seller",   // matches JWT, satisfies endpoint pre-check
            Name = "Hacked Name",
            Description = "Hacked Description",
            Price = 0.01m
        };

        // ACT — Submit the PUT as the attacker. The endpoint passes the JWT check (subject
        // matches body), invokes the handler, the handler loads the product, sees
        // product.SellerId != command.SellerId, returns false. Endpoint maps false → 404.
        var response = await client.PutAsJsonAsync($"/api/v1/products/{productId}", attack);

        // ASSERT — Four invariants:
        //  1) Response is 404, NOT 403 — the anti-enumeration property. A 403 would tell
        //     the attacker "this product exists, just not yours" and let them enumerate
        //     the product-ID space. 404 is indistinguishable from "no such product."
        //     CLAUDE.md "Security Requirements" lists this exact endpoint (PUT
        //     /products/{id}) as a reference template for this pattern.
        //  2-4) The stored row is untouched — name, price, and original seller all
        //     preserved. If the ownership check were missing, the attacker would have
        //     overwritten the product and these assertions would fail. The DB assertions
        //     go directly through the DbContext (not the API) so a buggy cache can't
        //     mask a real mutation.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var fromDb = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        fromDb.Name.Should().Be(originalName);
        fromDb.Price.Should().Be(originalPrice);
        fromDb.SellerId.Should().Be(ownerSellerId);
    }

    [Fact]
    public async Task PutProduct_byOwner_returns204AndPersistsTheChange()
    {
        // ARRANGE — Happy path comparison: same endpoint, called by the actual owner
        // (the seeded product's SellerId matches "test-seller", the JWT principal).
        // This test exists so the IDOR test above can't pass for the wrong reason
        // (e.g. if the endpoint were broken and returned 404 for every PUT, the IDOR
        // test would still "pass"). Together they prove the rejection is specific to
        // the ownership mismatch.
        var (productId, _, _) = await SeedProductAsync(
            sellerId: "test-seller", name: "Original", price: 50m);

        var client = _factory.CreateClient();
        var update = new
        {
            ProductId = productId,
            SellerId = "test-seller",
            Name = "Owner-Updated",
            Description = "Owner-Updated",
            Price = 75m
        };

        // ACT — Submit the PUT as the legitimate owner.
        var response = await client.PutAsJsonAsync($"/api/v1/products/{productId}", update);

        // ASSERT — Two invariants:
        //  1) Response is 204 NoContent — the success status for a PUT mutation.
        //  2) The stored row reflects the update — proves the handler ran the mutation
        //     and persisted it. If the handler always returned false (a regression),
        //     this assertion would catch it. The IDOR test pairs with this one to
        //     pin down "rejects only when ownership mismatches."
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var fromDb = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId);
        fromDb.Name.Should().Be("Owner-Updated");
        fromDb.Price.Should().Be(75m);
    }

    /// <summary>
    /// Seeds a category + product with the supplied seller id. Returns the product id and
    /// the original name/price so the caller can assert against the row after the test
    /// scenario runs (the "untouched after rejected write" invariant).
    /// </summary>
    private async Task<(Guid productId, string originalName, decimal originalPrice)> SeedProductAsync(
        string sellerId, string name, decimal price)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var category = Category.Create("AuthTestCategory-" + Guid.NewGuid(), "seeded for IDOR test");
        var product = Product.Create(
            name: name,
            description: "seeded for IDOR test",
            price: price,
            currency: "USD",
            categoryId: category.Id,
            sellerId: sellerId,
            stockQuantity: 10);

        db.Categories.Add(category);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return (product.Id, name, price);
    }
}
