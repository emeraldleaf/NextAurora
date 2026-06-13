using System.Diagnostics.Metrics;
using FluentValidation;
using Microsoft.Extensions.Logging;
using NextAurora.Contracts.Events;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Features;

/// <summary>
/// "Place order" vertical slice: command + validator + handler co-located. The saga's entry
/// point. This is the most involved handler in the system because order placement is a
/// multi-step operation:
///
/// <list type="number">
///   <item>Validate every requested line (product exists, is available, has enough stock) with
///         ONE batch <b>gRPC</b> call to CatalogService, then reserve the whole order's stock
///         with ONE atomic batch call (sync because we need a definitive answer before
///         continuing).</item>
///   <item>Build the <see cref="Order"/> aggregate via its factory (validates currency, lines,
///         buyer ID).</item>
///   <item>Add the aggregate to the tracked DbContext.</item>
///   <item>Publish <see cref="OrderPlacedEvent"/> so PaymentService and NotificationService can react.</item>
///   <item>Call <c>SaveChangesAsync</c> — this is what binds the entity write + the staged
///         outbox envelope into one DB transaction.</item>
/// </list>
///
/// <para>
/// <b>Transactional outbox — order matters.</b> <c>eventPublisher.PublishAsync</c> stages the
/// envelope into Wolverine's in-memory tracker; <c>context.SaveChangesAsync</c> then flushes
/// BOTH the new <see cref="Order"/> row AND the staged envelope into the SAME DB transaction
/// (via <c>UseEntityFrameworkCoreTransactions</c>). The publish must happen BEFORE the save —
/// the previous shape (save first, publish after) committed the entity alone and left a brief
/// window where the order was in the DB but no event was enqueued. A process death in that
/// window would stall the saga because PaymentService never sees the <c>OrderPlacedEvent</c>.
/// With publish-before-save the two writes commit atomically: either both land in the DB or
/// the transaction rolls back and the handler retries.
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
    OrderDbContext context,
    IEventPublisher eventPublisher,
    ICatalogClient catalogClient,
    ILogger<PlaceOrderHandler> logger)
{
    private static readonly Counter<long> OrdersPlaced =
        new Meter("NextAurora").CreateCounter<long>("orders.placed");

    public async Task<Guid> HandleAsync(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        var lines = new List<OrderLine>();

        // Two-phase, ONE gRPC round-trip each: validate the whole order, then reserve the whole
        // order. The phase split is intentional — if validation fails on any line, NO
        // reservation happens. Reservation itself is atomic all-or-nothing on the Catalog side
        // (single DB transaction over every line), so a failure on any line leaves zero
        // reservations — the partial-state compensation problem of the old per-line fan-out
        // shape is structurally gone, not just guarded against.
        var productIds = request.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = (await catalogClient.ValidateLinesAsync(productIds, cancellationToken))
            .ToDictionary(p => p.Id);

        foreach (var lineItem in request.Lines)
        {
            if (!products.TryGetValue(lineItem.ProductId, out var product))
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

        // Stock reservation phase — runs only if every line validated above. Atomic on the
        // Catalog side: every line reserves in one transaction or none do (optimistic
        // concurrency tokens guard each product row; one stale row rolls back all lines).
        var reserved = await catalogClient.ReserveLinesAsync(
            request.Lines.Select(l => new CatalogReserveLine(l.ProductId, l.Quantity)).ToList(),
            cancellationToken);

        if (!reserved)
        {
            logger.LogWarning("Failed to reserve stock for order with {LineCount} lines", request.Lines.Count);
            throw new InvalidOperationException("Failed to reserve stock for one or more requested products.");
        }

        // Build OrderLine entities. Notice: we use `product.Price` from CatalogService, NOT a
        // price the client sent. Server-side pricing — never trust client-submitted prices for
        // money calculations. See CLAUDE.md "Security Requirements → Server-controlled fields
        // are computed server-side, never trusted from the client".
        foreach (var lineItem in request.Lines)
        {
            var product = products[lineItem.ProductId];
            lines.Add(OrderLine.Create(product.Id, product.Name, lineItem.Quantity, product.Price));
        }

        var order = Order.Create(request.BuyerId, request.Currency, lines);
        await context.Orders.AddAsync(order, cancellationToken);

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

        // PUBLISH BEFORE SAVE — required for outbox atomicity. PublishAsync stages the envelope
        // into Wolverine's in-memory tracker; SaveChangesAsync below then flushes BOTH the new
        // Order row AND the staged envelope into the SAME DB transaction (via
        // UseEntityFrameworkCoreTransactions). If we saved first and published after, the entity
        // would commit alone — leaving a brief window where the order is in the DB but no event
        // is enqueued. A process death in that window stalls the saga because PaymentService
        // never sees the OrderPlacedEvent. See class summary for the full rationale.
        await eventPublisher.PublishAsync(@event, cancellationToken);

        // SaveChanges flushes the Order write AND the staged envelope into the same DB
        // transaction. Atomic — either both land in the DB or both roll back.
        await context.SaveChangesAsync(cancellationToken);

        OrdersPlaced.Add(1);
        return order.Id;
    }
}
