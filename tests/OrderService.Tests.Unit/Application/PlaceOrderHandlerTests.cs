using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NextAurora.Contracts.DTOs;
using NextAurora.Contracts.Events;
using OrderService.Application.Commands;
using OrderService.Application.Handlers;
using OrderService.Application.Interfaces;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Tests.Unit.Application;

public class PlaceOrderHandlerTests
{
    private readonly IOrderRepository _orderRepository = Substitute.For<IOrderRepository>();
    private readonly IEventPublisher _eventPublisher = Substitute.For<IEventPublisher>();
    private readonly ICatalogClient _catalogClient = Substitute.For<ICatalogClient>();
    private readonly ILogger<PlaceOrderHandler> _logger = Substitute.For<ILogger<PlaceOrderHandler>>();
    private readonly PlaceOrderHandler _sut;

    public PlaceOrderHandlerTests()
    {
        _sut = new PlaceOrderHandler(_orderRepository, _eventPublisher, _catalogClient, _logger);
    }

    private static PlaceOrderCommand CreateCommand(Guid? buyerId = null, string currency = "USD", List<PlaceOrderLineItem>? lines = null)
    {
        return new PlaceOrderCommand(
            buyerId ?? Guid.NewGuid(),
            currency,
            lines ?? [new PlaceOrderLineItem(Guid.NewGuid(), "Product", 1, 10m)]);
    }

    private void SetupProductAvailable(Guid productId, decimal price = 10m, int stock = 100)
    {
        _catalogClient.GetProductAsync(productId, Arg.Any<CancellationToken>())
            .Returns(new ProductDto
            {
                Id = productId,
                Name = "Test Product",
                Price = price,
                IsAvailable = true,
                StockQuantity = stock
            });
    }

    [Fact]
    public async Task Handle_WithValidCommand_CreatesOrderAndPublishesEvent()
    {
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 2, 10m)]);
        SetupProductAvailable(productId, price: 25m, stock: 10);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Should().NotBeEmpty();
        await _orderRepository.Received(1).AddAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(Arg.Any<OrderPlacedEvent>(), "order-events", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ThrowsInvalidOperationException()
    {
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 1, 10m)]);
        _catalogClient.GetProductAsync(productId, Arg.Any<CancellationToken>()).Returns((ProductDto?)null);

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not*found*");
    }

    [Fact]
    public async Task Handle_WhenProductUnavailable_ThrowsInvalidOperationException()
    {
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 1, 10m)]);
        _catalogClient.GetProductAsync(productId, Arg.Any<CancellationToken>())
            .Returns(new ProductDto { Id = productId, Name = "P", IsAvailable = false, StockQuantity = 0 });

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not currently available*");
    }

    [Fact]
    public async Task Handle_WhenInsufficientStock_ThrowsInvalidOperationException()
    {
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 100, 10m)]);
        SetupProductAvailable(productId, stock: 5);

        var act = () => _sut.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Insufficient stock*");
    }

    [Fact]
    public async Task Handle_UsesProductPriceFromCatalog()
    {
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 2, 999m)]);
        SetupProductAvailable(productId, price: 25m);

        await _sut.Handle(command, CancellationToken.None);

        await _orderRepository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.TotalAmount == 50m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SavesOrderBeforePublishingEvent()
    {
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 1, 10m)]);
        SetupProductAvailable(productId);

        var callOrder = new List<string>();
        _orderRepository.AddAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("save"));
        _eventPublisher.PublishAsync(Arg.Any<OrderPlacedEvent>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("publish"));

        await _sut.Handle(command, CancellationToken.None);

        callOrder.Should().ContainInOrder("save", "publish");
    }

    [Fact]
    public async Task Handle_EventContainsCorrectOrderDetails()
    {
        var productId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var command = CreateCommand(buyerId: buyerId, lines: [new PlaceOrderLineItem(productId, "P", 3, 10m)]);
        SetupProductAvailable(productId, price: 20m);

        await _sut.Handle(command, CancellationToken.None);

        await _eventPublisher.Received(1).PublishAsync(
            Arg.Is<OrderPlacedEvent>(e =>
                e.BuyerId == buyerId &&
                e.Currency == "USD" &&
                e.TotalAmount == 60m &&
                e.Lines.Count == 1),
            "order-events",
            Arg.Any<CancellationToken>());
    }
}
