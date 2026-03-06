using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using MediatR;
using NextAurora.Contracts.Events;
using OrderService.Application.Commands;
using OrderService.Application.Interfaces;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.Handlers;

public class PlaceOrderHandler(
    IOrderRepository orderRepository,
    IEventPublisher eventPublisher,
    ICatalogClient catalogClient,
    ILogger<PlaceOrderHandler> logger)
    : IRequestHandler<PlaceOrderCommand, Guid>
{
    private static readonly Counter<long> OrdersPlaced =
        new Meter("NextAurora").CreateCounter<long>("orders.placed");

    public async Task<Guid> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        var lines = new List<OrderLine>();

        foreach (var lineItem in request.Lines)
        {
            var product = await catalogClient.GetProductAsync(lineItem.ProductId, cancellationToken);

            if (product is null)
            {
                logger.LogWarning("Product {ProductId} not found during order placement", lineItem.ProductId);
                throw new InvalidOperationException("One or more requested products could not be found.");
            }

            if (!product.IsAvailable)
            {
                logger.LogWarning("Product {ProductId} is not available", lineItem.ProductId);
                throw new InvalidOperationException("One or more requested products are not currently available.");
            }

            if (product.StockQuantity < lineItem.Quantity)
            {
                logger.LogWarning("Insufficient stock for product {ProductId}. Requested: {Requested}, Available: {Available}",
                    lineItem.ProductId, lineItem.Quantity, product.StockQuantity);
                throw new InvalidOperationException("Insufficient stock for one or more requested products.");
            }

            var reserved = await catalogClient.ReserveStockAsync(lineItem.ProductId, lineItem.Quantity, cancellationToken);
            if (!reserved)
            {
                logger.LogWarning("Failed to reserve stock for product {ProductId}", lineItem.ProductId);
                throw new InvalidOperationException("Failed to reserve stock for one or more requested products.");
            }

            lines.Add(OrderLine.Create(product.Id, product.Name, lineItem.Quantity, product.Price));
        }

        var order = Order.Create(request.BuyerId, request.Currency, lines);
        await orderRepository.AddAsync(order, cancellationToken);

        var @event = new OrderPlacedEvent
        {
            OrderId = order.Id,
            BuyerId = order.BuyerId,
            PlacedAt = order.PlacedAt,
            TotalAmount = order.TotalAmount,
            Currency = order.Currency,
            Lines = order.Lines.Select(l => new OrderLineContract
            {
                ProductId = l.ProductId,
                ProductName = l.ProductName,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };

        await eventPublisher.PublishAsync(@event, "order-events", cancellationToken);
        OrdersPlaced.Add(1);
        return order.Id;
    }
}
