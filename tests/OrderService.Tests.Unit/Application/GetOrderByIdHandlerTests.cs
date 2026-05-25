using AwesomeAssertions;
using NextAurora.Contracts.DTOs;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using OrderService.Domain;
using OrderService.Features;

namespace OrderService.Tests.Unit.Application;

public class GetOrderByIdHandlerTests
{
    private readonly IOrderRepository _repository = Substitute.For<IOrderRepository>();
    private readonly GetOrderByIdHandler _sut;

    public GetOrderByIdHandlerTests()
    {
        _sut = new GetOrderByIdHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenOrderExists_ReturnsDtoFromReadProjection()
    {
        // ARRANGE — The handler is a one-line passthrough to GetSummaryByIdAsync, which projects
        // in EF and returns the DTO directly (no entity hop, no in-memory mapper — see
        // docs/cqrs-data-access.md). We stub the repo to return a fully-shaped DTO and verify
        // the handler returns it unchanged. The interesting *contract* the test pins down:
        //  - The handler must NOT call the entity-returning GetByIdAsync (that's the write-path
        //    loader for saga handlers); calling it on the read path would resurrect the
        //    materialize-then-map anti-pattern this rule exists to prevent.
        var orderId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var expected = new OrderSummaryDto
        {
            OrderId = orderId,
            BuyerId = buyerId,
            Status = nameof(OrderStatus.Placed),
            TotalAmount = 30m,
            Currency = "USD",
            PlacedAt = DateTime.UtcNow,
            Lines =
            [
                new OrderLineSummaryDto { ProductId = Guid.NewGuid(), ProductName = "Widget", Quantity = 2, UnitPrice = 15m }
            ]
        };
        _repository.GetSummaryByIdAsync(orderId, Arg.Any<CancellationToken>()).Returns(expected);

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetOrderByIdQuery(orderId), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) The DTO from the projection passes through unchanged (handler is a passthrough).
        //  2) The handler did NOT call GetByIdAsync — write loader stays off the read path.
        //     Calling it would reintroduce entity materialization on a read; the cqrs-data-access
        //     rule treats that as a hard violation. This assertion is the test-level enforcer.
        result.Should().BeSameAs(expected);
        await _repository.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsNull()
    {
        // ARRANGE — Read store returns null. The endpoint translates this to a 404.
        // Returning a sentinel like Guid.Empty would force every caller into special-case
        // handling; null is unambiguous "not found".
        _repository.GetSummaryByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetOrderByIdQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Null DTO surfaces as 404 at the endpoint.
        result.Should().BeNull();
    }
}
