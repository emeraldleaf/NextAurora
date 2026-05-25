using AwesomeAssertions;
using NextAurora.Contracts.DTOs;
using NSubstitute;
using OrderService.Domain;
using OrderService.Features;

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
    public async Task Handle_WhenBuyerHasOrders_ReturnsDtosFromReadProjection()
    {
        // ARRANGE — Handler is a one-line passthrough to GetSummariesByBuyerIdAsync, which
        // projects in EF and returns DTOs directly. No entity hop. We stub two DTOs and verify
        // both make the round trip + the buyer-scope filter still holds at the API surface
        // (belt + suspenders; the SQL Where clause is the actual enforcement).
        var buyerId = Guid.NewGuid();
        var summaries = new List<OrderSummaryDto>
        {
            new() { OrderId = Guid.NewGuid(), BuyerId = buyerId, Status = "Placed", PlacedAt = DateTime.UtcNow, Lines = [] },
            new() { OrderId = Guid.NewGuid(), BuyerId = buyerId, Status = "Placed", PlacedAt = DateTime.UtcNow, Lines = [] }
        };
        _repository.GetSummariesByBuyerIdAsync(buyerId, 1, 50, Arg.Any<CancellationToken>()).Returns(summaries);

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetOrdersByBuyerQuery(buyerId), CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) Both DTOs round-trip.
        //  2) Every returned summary's BuyerId matches the requested buyer — guards against the
        //     repository contract ever being broken (cross-buyer leak surface area).
        //  3) The entity-returning GetByIdAsync stays untouched on the read path — the
        //     write-loader anti-pattern must not creep back. Asserted as a hard rule below.
        result.Should().HaveCount(2);
        result.Should().OnlyContain(r => r.BuyerId == buyerId);
        await _repository.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenBuyerHasNoOrders_ReturnsEmptyList()
    {
        // ARRANGE — New buyer, never placed an order.
        _repository
            .GetSummariesByBuyerIdAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OrderSummaryDto>());

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetOrdersByBuyerQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Empty (non-null) list. Null would force every API consumer to handle
        // an extra case.
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForwardsBuyerIdAndPaginationToReadProjection()
    {
        // ARRANGE — Pagination + buyer scoping are delegated to the projection method so the
        // SQL does the work. If a future refactor pushed filtering into the handler ("filter
        // GetAll in memory by BuyerId"), this test fails immediately — catching both a perf
        // regression AND a cross-buyer-leak surface area.
        _repository
            .GetSummariesByBuyerIdAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<OrderSummaryDto>());
        var buyerId = Guid.NewGuid();

        // ACT — Run the handler with non-default pagination.
        var result = await _sut.HandleAsync(new GetOrdersByBuyerQuery(buyerId, Page: 4, PageSize: 25), CancellationToken.None);

        // ASSERT — Buyer id + pagination flow straight through to the projection method + the
        // empty result surfaces to the caller (handler doesn't synthesize anything on top).
        await _repository.Received(1).GetSummariesByBuyerIdAsync(buyerId, 4, 25, Arg.Any<CancellationToken>());
        result.Should().BeEmpty();
    }
}
