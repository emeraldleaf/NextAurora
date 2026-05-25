using AwesomeAssertions;
using CatalogService.Application.Commands;
using CatalogService.Application.Handlers;
using CatalogService.Application.Interfaces;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Tests.Unit.Builders;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace CatalogService.Tests.Unit.Application;

public class UpdateProductHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IProductCache _cache = Substitute.For<IProductCache>();
    private readonly UpdateProductHandler _sut;

    public UpdateProductHandlerTests()
    {
        _sut = new UpdateProductHandler(_repository, _cache);
    }

    [Fact]
    public async Task Handle_WithMatchingSeller_UpdatesProductAndInvalidatesCache()
    {
        // ARRANGE — Build a real Product (so the domain's UpdateDetails rules actually run),
        // and craft a command whose SellerId matches the stored product's owner. The repo
        // returns the product so the handler can mutate + persist it.
        var product = ProductBuilder.Default().Build();
        var command = new UpdateProductCommand(
            product.Id,
            product.SellerId,
            "Updated Name",
            "Updated Description",
            99.99m);
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        // ACT — Run the handler.
        var result = await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Four invariants:
        //  1) Handler returns true — success. The endpoint translates true to 204.
        //  2) Domain mutation happened (the loaded entity now carries the new fields).
        //     We check via the entity reference because the handler mutates in place.
        //  3) The repository saved the (mutated) aggregate.
        //  4) The cache entry for this product was invalidated — without this, stale
        //     ProductDto reads would survive the write and the L1/L2 caches would diverge
        //     from the DB until TTL. CLAUDE.md "Performance Rules" requires invalidation
        //     in the write path, not via TTL.
        result.Should().BeTrue();
        product.Name.Should().Be("Updated Name");
        product.Description.Should().Be("Updated Description");
        product.Price.Should().Be(99.99m);
        await _repository.Received(1).UpdateAsync(product, Arg.Any<CancellationToken>());
        await _cache.Received(1).InvalidateAsync(product.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ReturnsFalseAndSkipsCacheInvalidation()
    {
        // ARRANGE — Repository returns null so the handler treats this as "no such product".
        // Per CLAUDE.md's IDOR contract, the handler returns false (NOT throws). The endpoint
        // maps false to 404, which is indistinguishable from the seller-mismatch case below —
        // that's the anti-enumeration property.
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();
        var command = new UpdateProductCommand(
            Guid.NewGuid(), "any-seller", "n", "d", 10m);

        // ACT — Run the handler.
        var result = await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) Result is false — the endpoint will translate this to 404.
        //  2) No cache call — invalidating a non-existent product would be harmless but
        //     misleading in observability ("we tried to write something"). The short-circuit
        //     keeps the cache layer untouched on the rejected path.
        result.Should().BeFalse();
        await _cache.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSellerIdDoesNotMatch_ReturnsFalseAndDoesNotMutate()
    {
        // ARRANGE — Defense-in-depth IDOR scenario: the endpoint already checks the JWT
        // subject against command.SellerId, but a caller could submit THEIR own seller id
        // paired with someone else's product id. The handler MUST catch this — without the
        // check, any authenticated seller could overwrite any product. (CWE-639 — IDOR.)
        //
        // Why false → 404 (not throw → 403): CLAUDE.md "Security Requirements" treats this
        // PUT /products/{id} endpoint as the canonical seller-scope reference template for
        // the anti-enumeration pattern. Returning 403 here would leak existence ("the product
        // is there, just not yours") and let an attacker enumerate the product-ID space.
        // 404 is indistinguishable from "doesn't exist" (the test above) — that's the
        // anti-enumeration property. Both branches go through the same false return so the
        // caller cannot distinguish them at any layer.
        var product = ProductBuilder.Default().Build();
        var attackerSellerId = "different-seller-" + Guid.NewGuid();
        var originalName = product.Name;
        var originalPrice = product.Price;
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        var command = new UpdateProductCommand(
            product.Id, attackerSellerId, "Hacked", "Hacked", 0.01m);

        // ACT — Run the handler.
        var result = await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Four invariants:
        //  1) Result is false — endpoint translates to 404, indistinguishable from
        //     "product not found" (anti-enumeration).
        //  2) Stored entity untouched — name and price are still what they were. If the
        //     update ran before the seller check, an attacker could mutate any product
        //     and the test would catch it here.
        //  3) UpdateAsync NOT called — proves the persistence write was skipped, not just
        //     that the in-memory mutation was skipped.
        //  4) InvalidateAsync NOT called — proves the cache layer wasn't touched on the
        //     security-rejected path. A cache invalidation on a rejected write would
        //     either be harmless noise OR (if a concurrent reader hit the same key) cause
        //     a needless DB round-trip to repopulate an unchanged entry.
        result.Should().BeFalse();
        product.Name.Should().Be(originalName);
        product.Price.Should().Be(originalPrice);
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidatesCacheAfterRepositoryUpdate()
    {
        // ARRANGE — Order matters: invalidate AFTER save. If invalidation came first, a
        // concurrent reader could repopulate the cache with the OLD value between our
        // invalidate and our save — the cache would then serve stale data indefinitely
        // (or until TTL). We capture the call order on the substitutes to prove the
        // sequence at the unit level, since at this layer the EF/cache are mocked.
        var product = ProductBuilder.Default().Build();
        var command = new UpdateProductCommand(
            product.Id, product.SellerId, "New", "New", 50m);
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        var callOrder = new List<string>();
        _repository.UpdateAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("update"));
        _cache.InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("invalidate"));

        // ACT — Run the handler.
        var result = await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) "update" must come strictly before "invalidate" (the ordering rationale).
        //  2) Result is true — happy path; endpoint maps to 204. The order assertion
        //     alone would pass even if the handler returned false by mistake, so this
        //     pins the success-return contract too.
        callOrder.Should().ContainInOrder("update", "invalidate");
        result.Should().BeTrue();
    }
}
