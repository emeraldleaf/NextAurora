using AwesomeAssertions;
using NSubstitute;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Tests.Unit.Builders;

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
    public async Task Handle_WhenOrderExists_ReturnsMappedDto()
    {
        // ARRANGE — Build a real Order via the domain factory so the projection runs through
        // real data (TotalAmount, PlacedAt, lines). The repo returns the entity; the handler
        // delegates to OrderSummaryMapper, which stringifies the enum at the DTO boundary
        // (the API never returns the enum integer — see the mapper's doc comment).
        var buyerId = Guid.NewGuid();
        var line = OrderLineBuilder.Default()
            .WithProductName("Widget")
            .WithQuantity(2)
            .WithUnitPrice(15m)
            .Build();
        var order = OrderBuilder.Default()
            .WithBuyerId(buyerId)
            .WithLines([line])
            .Build();
        _repository.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);

        // ACT
        var result = await _sut.HandleAsync(new GetOrderByIdQuery(order.Id), CancellationToken.None);

        // ASSERT — Five invariants:
        //  1) Result is non-null (the order was found).
        //  2) OrderId matches the input — defensive check that we returned the right one.
        //  3) BuyerId round-trips — the API surface needs it for the ownership check upstream.
        //  4) Status is the STRING form ("Placed") — proves the enum-to-string conversion at
        //     the DTO boundary. If a refactor accidentally exposed the int, this catches it.
        //  5) Lines are mapped, including ProductName + Quantity. Total/aggregate fields are
        //     covered by the domain tests for Order.Create.
        result.Should().NotBeNull();
        result!.OrderId.Should().Be(order.Id);
        result.BuyerId.Should().Be(buyerId);
        result.Status.Should().Be(nameof(OrderStatus.Placed));
        result.Lines.Should().ContainSingle()
            .Which.Should().Match<NextAurora.Contracts.DTOs.OrderLineSummaryDto>(l =>
                l.ProductName == "Widget" && l.Quantity == 2 && l.UnitPrice == 15m);
    }

    [Fact]
    public async Task Handle_WhenOrderNotFound_ReturnsNull()
    {
        // ARRANGE — Repository returns null. The endpoint translates this to a 404.
        // Returning a sentinel like Guid.Empty would force every caller into special-case
        // handling; null is unambiguous "not found".
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        // ACT
        var result = await _sut.HandleAsync(new GetOrderByIdQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT
        result.Should().BeNull();
    }
}
