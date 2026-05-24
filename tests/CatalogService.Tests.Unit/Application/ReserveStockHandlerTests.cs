using AwesomeAssertions;
using CatalogService.Application.Commands;
using CatalogService.Application.Handlers;
using CatalogService.Application.Interfaces;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Tests.Unit.Builders;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class ReserveStockHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IProductCache _cache = Substitute.For<IProductCache>();
    private readonly ReserveStockHandler _sut;

    public ReserveStockHandlerTests()
    {
        _sut = new ReserveStockHandler(_repository, _cache);
    }

    [Fact]
    public async Task Handle_WithEnoughStock_DecrementsAndInvalidatesCache()
    {
        // ARRANGE — Build a real Product with 10 units in stock. OrderService is asking for
        // 3 via gRPC. The handler should subtract 3, save, then invalidate the cache because
        // both StockQuantity and the derived IsAvailable live on ProductDto.
        var product = ProductBuilder.Default().WithStockQuantity(10).Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);
        var command = new ReserveStockCommand(product.Id, 3);

        // ACT
        var result = await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Four invariants:
        //  1) Returns true (reservation succeeded — OrderService treats false as "couldn't").
        //  2) Domain stock decremented to 7. AdjustStock also flips IsAvailable correctly
        //     (still true here since 7 > 0).
        //  3) Repository.UpdateAsync was called — the xmin/RowVersion concurrency token
        //     protects against two parallel reservations both winning. That's tested via
        //     integration; here we just verify the save path runs.
        //  4) Cache invalidated AFTER the save (see UpdateProductHandlerTests for the
        //     race-window rationale).
        result.Should().BeTrue();
        product.StockQuantity.Should().Be(7);
        product.IsAvailable.Should().BeTrue();
        await _repository.Received(1).UpdateAsync(product, Arg.Any<CancellationToken>());
        await _cache.Received(1).InvalidateAsync(product.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenReservingAllRemainingStock_FlipsIsAvailableToFalse()
    {
        // ARRANGE — Edge case: ordering the last unit. Verifies that the derived IsAvailable
        // flag flips correctly when stock hits zero (the domain rule "IsAvailable tracks
        // StockQuantity > 0" must hold). If a future refactor split stock and availability,
        // this test would fail and catch the regression.
        var product = ProductBuilder.Default().WithStockQuantity(5).Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        // ACT
        var result = await _sut.HandleAsync(new ReserveStockCommand(product.Id, 5), CancellationToken.None);

        // ASSERT
        result.Should().BeTrue();
        product.StockQuantity.Should().Be(0);
        product.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ReturnsFalseWithoutSaveOrInvalidation()
    {
        // ARRANGE — Repository returns null. The handler treats this as a soft failure
        // (returns false) rather than throwing — OrderService's caller can then surface a
        // user-friendly "product unavailable" without an exception roundtrip.
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Product?)null);

        // ACT
        var result = await _sut.HandleAsync(new ReserveStockCommand(Guid.NewGuid(), 1), CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) Returns false — caller treats as "couldn't reserve".
        //  2) No save call — there's nothing to save.
        //  3) No cache invalidation — invalidating a non-existent product is harmless
        //     but pollutes observability (looks like we wrote when we didn't).
        result.Should().BeFalse();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenInsufficientStock_ReturnsFalseAndDoesNotMutate()
    {
        // ARRANGE — Product has 2 in stock, OrderService asks for 5. The handler should
        // refuse cleanly. The stock check here is a fast-path; the real correctness backstop
        // is the xmin/RowVersion token on the aggregate (covered by integration tests),
        // which prevents two simultaneous "I checked, 2 is enough" reservations from both
        // winning.
        var product = ProductBuilder.Default().WithStockQuantity(2).Build();
        var originalStock = product.StockQuantity;
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        // ACT
        var result = await _sut.HandleAsync(new ReserveStockCommand(product.Id, 5), CancellationToken.None);

        // ASSERT — Returns false, entity unchanged, no save, no invalidation.
        result.Should().BeFalse();
        product.StockQuantity.Should().Be(originalStock);
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidatesCacheAfterRepositoryUpdate()
    {
        // ARRANGE — Same write-then-invalidate ordering rule as UpdateProductHandler.
        // If invalidate runs first, a concurrent reader can repopulate the cache from
        // the pre-update DB row in the millisecond gap before the save commits — leaving
        // the cache stale until TTL. We prove the ordering at the unit level here.
        var product = ProductBuilder.Default().WithStockQuantity(10).Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        var callOrder = new List<string>();
        _repository.UpdateAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("update"));
        _cache.InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("invalidate"));

        // ACT
        await _sut.HandleAsync(new ReserveStockCommand(product.Id, 1), CancellationToken.None);

        // ASSERT
        callOrder.Should().ContainInOrder("update", "invalidate");
    }
}
