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
        await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) Domain mutation happened (the loaded entity now carries the new fields).
        //     We check via the entity reference because the handler mutates in place.
        //  2) The repository saved the (mutated) aggregate.
        //  3) The cache entry for this product was invalidated — without this, stale
        //     ProductDto reads would survive the write and the L1/L2 caches would diverge
        //     from the DB until TTL. CLAUDE.md "Performance Rules" requires invalidation
        //     in the write path, not via TTL.
        product.Name.Should().Be("Updated Name");
        product.Description.Should().Be("Updated Description");
        product.Price.Should().Be(99.99m);
        await _repository.Received(1).UpdateAsync(product, Arg.Any<CancellationToken>());
        await _cache.Received(1).InvalidateAsync(product.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ThrowsAndSkipsCacheInvalidation()
    {
        // ARRANGE — Repository returns null so the handler treats this as "no such product".
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();
        var command = new UpdateProductCommand(
            Guid.NewGuid(), "any-seller", "n", "d", 10m);

        // ACT — Wrap the call in a delegate so AwesomeAssertions can capture the exception.
        var act = () => _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — The handler throws InvalidOperationException. Critically, NO cache call:
        // invalidating a non-existent product would be harmless but also misleading — it
        // signals "we tried to write something" in observability. We never reach the cache
        // line because the throw happens first.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
        await _cache.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSellerIdDoesNotMatch_ThrowsUnauthorizedAndDoesNotMutate()
    {
        // ARRANGE — Defense-in-depth scenario: the endpoint already checks the JWT subject
        // against command.SellerId, but a caller could submit THEIR own seller id paired
        // with someone else's product id. The handler MUST catch this — without the check,
        // any authenticated seller could overwrite any product. (CWE-639 — IDOR.)
        //
        // NOTE on 403 vs 404: CLAUDE.md "Security Requirements" mandates null → 404 for
        // BUYER-SCOPED READS (anti-enumeration: returning 403 leaks that the entity
        // exists). This handler is a SELLER-SCOPED WRITE — the project intentionally
        // returns 403 here (GlobalExceptionHandler maps UnauthorizedAccessException
        // to 403; see docs/STATUS.md "CatalogService seller authorization"). The
        // reasoning: the attacker is authenticated, the failure mode is "you're not
        // authorized to write this resource", and 403 is the correct HTTP semantic.
        // If we ever decide seller-scoped writes should also use the anti-enumeration
        // pattern, this test + the handler + GlobalExceptionHandler change together.
        var product = ProductBuilder.Default().Build();
        var attackerSellerId = "different-seller-" + Guid.NewGuid();
        var originalName = product.Name;
        var originalPrice = product.Price;
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        var command = new UpdateProductCommand(
            product.Id, attackerSellerId, "Hacked", "Hacked", 0.01m);

        // ACT — Wrap so AwesomeAssertions can inspect the thrown exception.
        var act = () => _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) UnauthorizedAccessException is thrown (GlobalExceptionHandler maps it to 403).
        //  2) The stored entity is untouched — name and price are still what they were.
        //  3) Neither UpdateAsync nor InvalidateAsync was called. If either ran, an attacker
        //     could either persist a malicious mutation OR poison the cache by triggering a
        //     re-read of an unmutated product (less harmful but still a side effect we don't
        //     want on a security-rejected request).
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
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
        await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — "update" must come strictly before "invalidate".
        callOrder.Should().ContainInOrder("update", "invalidate");
    }
}
