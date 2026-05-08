using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using OrderService.Application.Commands;
using OrderService.Application.Interfaces;
using OrderService.Domain.Entities;
using OrderService.Domain.Interfaces;

namespace OrderService.Application.Handlers;

/// <summary>
/// Handles the <see cref="PlaceOrderCommand"/> — the saga's entry point. This is the most
/// involved handler in the system because order placement is a multi-step operation:
///
/// <list type="number">
///   <item>For each requested line, validate the product exists, is available, and has enough stock,
///         then reserve that stock — all over <b>gRPC</b> to the CatalogService (sync because
///         we need a definitive answer before continuing).</item>
///   <item>Build the <see cref="Order"/> aggregate via its factory (validates currency, lines,
///         buyer ID).</item>
///   <item>Persist the order via the repository.</item>
///   <item>Publish <see cref="OrderPlacedEvent"/> so PaymentService and NotificationService can react.</item>
/// </list>
///
/// <para>
/// <b>SOLID — SRP:</b> this class does one thing: orchestrate "place an order". It doesn't
/// validate input formats (FluentValidation does that before we run), doesn't know about HTTP
/// (the endpoint adapts), and doesn't know about Service Bus internals (Wolverine does).
/// It composes <c>ICatalogClient</c> + <c>IOrderRepository</c> + <c>IEventPublisher</c> — all
/// abstractions, all injected (Dependency Inversion).
/// </para>
/// <para>
/// <b>Performance — gRPC over REST for product validation:</b> we call CatalogService over gRPC
/// rather than HTTP/JSON because the round-trip happens on the synchronous order-placement path
/// and we hit it once per line. Binary protobuf + HTTP/2 multiplexing wins on serialization
/// cost and connection overhead.
/// </para>
/// <para>
/// <b>Transactional outbox:</b> in the current Wolverine setup, when this handler returns,
/// Wolverine wraps the <c>SaveChanges</c> from <c>orderRepository.AddAsync</c> and the
/// <c>eventPublisher.PublishAsync</c> together — the event is staged into the
/// <c>wolverine</c> schema in the SAME transaction as the order write. If publishing to Service
/// Bus fails later, the entity write rolls back too. No more "order saved but PaymentService
/// never heard about it" failure mode. See <c>docs/performance-and-data-correctness.md</c>.
/// </para>
/// <para>
/// <b>Failure semantics:</b> if any line fails validation or stock reservation, we throw and
/// nothing persists. This is intentionally simple — there's no partial-success state. If a
/// reservation succeeded for an earlier line in the same command and a later line fails, that
/// reservation will eventually expire on the Catalog side (or the user retries and a new
/// reservation overwrites). Distributed-rollback compensation isn't worth the complexity at
/// our current scale.
/// </para>
/// </summary>
public class PlaceOrderHandler(
    IOrderRepository orderRepository,
    IEventPublisher eventPublisher,
    ICatalogClient catalogClient,
    ILogger<PlaceOrderHandler> logger)
{
    // OpenTelemetry counter for observability dashboards. Every successful placement increments;
    // failures throw before reaching the increment, so the counter reflects business success rate
    // rather than handler-invocation rate.
    private static readonly Counter<long> OrdersPlaced =
        new Meter("NextAurora").CreateCounter<long>("orders.placed");

    public async Task<Guid> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        var lines = new List<OrderLine>();

        // Validate each line independently. Three checks per line, in this order:
        // 1) does the product exist?
        // 2) is it currently available?
        // 3) is there enough stock?
        // Then a 4th call reserves the stock atomically on the Catalog side.
        // Any failure throws — order is never persisted partially.
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

            // Stock reservation is the side effect that matters most: this decrements Catalog's
            // stock count. If two simultaneous orders try to reserve the same item with limited
            // stock, exactly one wins (Catalog's optimistic concurrency token enforces it) and
            // the loser gets `false` here.
            var reserved = await catalogClient.ReserveStockAsync(lineItem.ProductId, lineItem.Quantity, cancellationToken);
            if (!reserved)
            {
                logger.LogWarning("Failed to reserve stock for product {ProductId}", lineItem.ProductId);
                throw new InvalidOperationException("Failed to reserve stock for one or more requested products.");
            }

            // Notice: we use `product.Price` from CatalogService, NOT a price the client sent.
            // Server-side pricing — never trust client-submitted prices for money calculations.
            // See CLAUDE.md.
            lines.Add(OrderLine.Create(product.Id, product.Name, lineItem.Quantity, product.Price));
        }

        // Domain factory builds and validates the full Order. If the buyer ID is empty, currency
        // missing, or zero lines (somehow), this throws before we hit the database.
        var order = Order.Create(request.BuyerId, request.Currency, lines);
        await orderRepository.AddAsync(order, cancellationToken);

        // Build the cross-service event. Note this is a separate type from the domain entity:
        // events live in NextAurora.Contracts so other services can deserialize them without
        // referencing OrderService.Domain.
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

        // Wolverine's outbox stages this in the wolverine.outgoing_envelopes table, in the same
        // transaction as the order save. After the handler returns successfully, Wolverine
        // commits the transaction and a background dispatcher pushes the event to Service Bus.
        await eventPublisher.PublishAsync(@event, cancellationToken);
        OrdersPlaced.Add(1);
        return order.Id;
    }
}
