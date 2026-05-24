using AwesomeAssertions;
using NSubstitute;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Application;

public class GetOrdersByBuyerHandlerTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly GetOrdersByBuyerHandler _sut;

    public GetOrdersByBuyerHandlerTests()
    {
        _sut = new GetOrdersByBuyerHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenBuyerHasOrders_ReturnsMappedSummaries()
    {
        // ARRANGE — Two real orders for a single buyer. The handler delegates to the same
        // OrderSummaryMapper as GetOrderById; we mostly verify the LIST shape rather than
        // re-testing the per-row mapping (already covered in GetOrderByIdHandlerTests).
        var buyerId = Guid.NewGuid();
        var o1 = OrderBuilder.Default().WithBuyerId(buyerId).Build();
        var o2 = OrderBuilder.Default().WithBuyerId(buyerId).Build();
        _repository
            .GetByBuyerIdAsync(buyerId, 1, 50, Arg.Any<CancellationToken>())
            .Returns(new List<Order> { o1, o2 });

        // ACT
        var result = await _sut.HandleAsync(new GetOrdersByBuyerQuery(buyerId), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) Both orders make the round trip.
        //  2) Every returned summary's BuyerId is the requested buyer — proves we don't
        //     leak someone else's orders if the repository contract were ever broken. The
        //     repository itself enforces this filter; this is belt + suspenders.
        result.Should().HaveCount(2);
        result.Should().OnlyContain(r => r.BuyerId == buyerId);
    }

    [Fact]
    public async Task Handle_WhenBuyerHasNoOrders_ReturnsEmptyList()
    {
        // ARRANGE — New buyer, never placed an order.
        _repository
            .GetByBuyerIdAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Order>());

        // ACT
        var result = await _sut.HandleAsync(new GetOrdersByBuyerQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Empty (non-null) list. Null would force every API consumer to handle
        // an extra case.
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForwardsBuyerIdAndPaginationToRepository()
    {
        // ARRANGE — Pagination + buyer scoping are delegated to the repository so the SQL
        // does the work. If a future refactor pushed filtering into the handler ("filter
        // GetAllAsync results in memory by BuyerId"), this test fails immediately —
        // catching both a perf regression AND a potential cross-buyer-leak surface area.
        _repository
            .GetByBuyerIdAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Order>());
        var buyerId = Guid.NewGuid();

        // ACT
        await _sut.HandleAsync(new GetOrdersByBuyerQuery(buyerId, Page: 4, PageSize: 25), CancellationToken.None);

        // ASSERT
        await _repository.Received(1).GetByBuyerIdAsync(buyerId, 4, 25, Arg.Any<CancellationToken>());
    }
}
