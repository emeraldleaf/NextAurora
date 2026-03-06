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
        _catalogClient.ReserveStockAsync(productId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task Handle_WithValidCommand_CreatesOrderAndPublishesEvent()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 2, 10m)]);
        SetupProductAvailable(productId, price: 25m, stock: 10);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.Should().NotBeEmpty();
        await _orderRepository.Received(1).AddAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
        await _eventPublisher.Received(1).PublishAsync(Arg.Any<OrderPlacedEvent>(), "order-events", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ThrowsInvalidOperationException()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 1, 10m)]);
        _catalogClient.GetProductAsync(productId, Arg.Any<CancellationToken>()).Returns((ProductDto?)null);

        // Act
        var act = () => _sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not*found*");
    }

    [Fact]
    public async Task Handle_WhenProductUnavailable_ThrowsInvalidOperationException()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 1, 10m)]);
        _catalogClient.GetProductAsync(productId, Arg.Any<CancellationToken>())
            .Returns(new ProductDto { Id = productId, Name = "P", IsAvailable = false, StockQuantity = 0 });

        // Act
        var act = () => _sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not currently available*");
    }

    [Fact]
    public async Task Handle_WhenInsufficientStock_ThrowsInvalidOperationException()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 100, 10m)]);
        SetupProductAvailable(productId, stock: 5);

        // Act
        var act = () => _sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Insufficient stock*");
    }

    [Fact]
    public async Task Handle_ReservesStockViaCatalogClient()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 3, 10m)]);
        SetupProductAvailable(productId, stock: 10);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _catalogClient.Received(1).ReserveStockAsync(productId, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenStockReservationFails_ThrowsInvalidOperationException()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 2, 10m)]);
        _catalogClient.GetProductAsync(productId, Arg.Any<CancellationToken>())
            .Returns(new ProductDto { Id = productId, Name = "P", Price = 10m, IsAvailable = true, StockQuantity = 100 });
        _catalogClient.ReserveStockAsync(productId, 2, Arg.Any<CancellationToken>())
            .Returns(false);

        // Act
        var act = () => _sut.Handle(command, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*reserve stock*");
    }

    [Fact]
    public async Task Handle_UsesProductPriceFromCatalog()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var command = CreateCommand(lines: [new PlaceOrderLineItem(productId, "P", 2, 999m)]);
        SetupProductAvailable(productId, price: 25m);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _orderRepository.Received(1).AddAsync(
            Arg.Is<Order>(o => o.TotalAmount == 50m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SavesOrderBeforePublishingEvent()
    {
        // Arrange
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

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        callOrder.Should().ContainInOrder("save", "publish");
    }

    [Fact]
    public async Task Handle_EventContainsCorrectOrderDetails()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var command = CreateCommand(buyerId: buyerId, lines: [new PlaceOrderLineItem(productId, "P", 3, 10m)]);
        SetupProductAvailable(productId, price: 20m);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
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
