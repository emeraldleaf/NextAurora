using System.Diagnostics.Metrics;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using OrderService.Domain;

namespace OrderService.Features;

/// <summary>
/// "Place order" vertical slice: command + validator + handler co-located. The saga's entry
/// point. This is the most involved handler in the system because order placement is a
/// multi-step operation:
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
/// <b>Transactional outbox:</b> when this handler returns, Wolverine wraps the <c>SaveChanges</c>
/// from <c>orderRepository.AddAsync</c> and the <c>eventPublisher.PublishAsync</c> together —
/// the event is staged into the <c>wolverine</c> schema in the SAME transaction as the order
/// write. If publishing to Service Bus fails later, the entity write rolls back too.
/// </para>
/// </summary>
public record PlaceOrderCommand(
    Guid BuyerId,
    string Currency,
    List<PlaceOrderLineItem> Lines);

public record PlaceOrderLineItem(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

public class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(x => x.BuyerId).NotEmpty();
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.Lines)
            .NotEmpty()
            .WithMessage("Order must contain at least one line item.");

        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
    }
}

public class PlaceOrderHandler(
    IOrderRepository orderRepository,
    IEventPublisher eventPublisher,
    ICatalogClient catalogClient,
    ILogger<PlaceOrderHandler> logger)
{
    private static readonly Counter<long> OrdersPlaced =
        new Meter("NextAurora").CreateCounter<long>("orders.placed");

    public async Task<Guid> HandleAsync(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        var lines = new List<OrderLine>();

        // Two-phase: validate all lines in parallel first, then reserve in parallel. The phase
        // split is intentional — if validation fails on any line, NO reservation happens. The
        // previous sequential code could reserve N-1 items before line N failed validation,
        // leaving stranded reservations on Catalog. This shape eliminates that partial-commit
        // path. Parallel reservation can still leave partial state if some reservations succeed
        // before another fails on Catalog's optimistic-concurrency check — that compensation
        // path is a known gap, see STATUS.md "Open issues".
        //
        // Safety note: parallelism here is over gRPC client calls only. The OrderService DbContext
        // is NOT touched in this block (no order is persisted yet), so the CLAUDE.md
        // "DbContext is not thread-safe" rule is satisfied — each gRPC call hits Catalog where
        // it gets its own per-request DbContext scope.
        var products = await Task.WhenAll(request.Lines.Select(line =>
            catalogClient.GetProductAsync(line.ProductId, cancellationToken)));

        for (int i = 0; i < request.Lines.Count; i++)
        {
            var lineItem = request.Lines[i];
            var product = products[i];

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
        }

        // Stock reservation phase — runs only if every line validated above. Optimistic
        // concurrency on the Catalog side ensures exactly one of two simultaneous reservations
        // wins per product.
        var reservations = await Task.WhenAll(request.Lines.Select(line =>
            catalogClient.ReserveStockAsync(line.ProductId, line.Quantity, cancellationToken)));

        for (int i = 0; i < reservations.Length; i++)
        {
            if (!reservations[i])
            {
                logger.LogWarning("Failed to reserve stock for product {ProductId}", request.Lines[i].ProductId);
                throw new InvalidOperationException("Failed to reserve stock for one or more requested products.");
            }
        }

        // Build OrderLine entities. Notice: we use `product.Price` from CatalogService, NOT a
        // price the client sent. Server-side pricing — never trust client-submitted prices for
        // money calculations. See CLAUDE.md.
        for (int i = 0; i < request.Lines.Count; i++)
        {
            var product = products[i]!; // null-forgiving: throw-on-null check above
            var lineItem = request.Lines[i];
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

        await eventPublisher.PublishAsync(@event, cancellationToken);
        OrdersPlaced.Add(1);
        return order.Id;
    }
}
