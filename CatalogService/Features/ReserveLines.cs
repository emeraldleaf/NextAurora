using CatalogService.Domain;
using CatalogService.Infrastructure.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Features;

/// <summary>
/// "Reserve order lines" vertical slice — the batch, <b>atomic all-or-nothing</b> sibling of
/// <see cref="ReserveStockHandler"/>. Called via gRPC from OrderService during order placement:
/// every line in the order is reserved in one DB transaction, or none are.
///
/// <para>
/// <b>Why atomicity matters here:</b> the previous per-line reservation fan-out could leave
/// partial state — lines 1–4 reserved, line 5 failed — which required compensation logic on
/// the caller that never actually existed (the known gap in the old PlaceOrder shape). With
/// one tracked load + one <c>SaveChangesAsync</c>, EF wraps all stock mutations in a single
/// transaction: a failure on any line (insufficient stock, missing product, concurrency loss)
/// rolls back every line. There is no partial outcome to compensate.
/// </para>
/// <para>
/// <b>Concurrency story:</b> same as <see cref="ReserveStockHandler"/> — the Postgres
/// <c>xmin</c> token on each Product row. If ANY of the loaded products was mutated between
/// our load and our save, <see cref="DbUpdateConcurrencyException"/> fires, nothing commits,
/// and we return false; the caller aborts placement. Returning false (not rethrowing) keeps
/// the gRPC contract "success means reserved; failure means nothing happened".
/// </para>
/// <para>
/// <b>Duplicate product IDs</b> (the same product on two order lines) are aggregated by
/// summing quantities before the stock check, so the check is against the order's true total
/// demand per product.
/// </para>
/// </summary>
public record ReserveLinesCommand(List<ReserveLine> Lines);

public record ReserveLine(Guid ProductId, int Quantity);

public class ReserveLinesCommandValidator : AbstractValidator<ReserveLinesCommand>
{
    public ReserveLinesCommandValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleFor(x => x.Lines.Count).LessThanOrEqualTo(100);
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0).LessThanOrEqualTo(10_000);
        });
    }
}

public class ReserveLinesHandler(CatalogDbContext context, IProductCache cache)
{
    public async Task<bool> HandleAsync(ReserveLinesCommand request, CancellationToken cancellationToken)
    {
        var demand = request.Lines
            .GroupBy(l => l.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

        var ids = demand.Keys.ToList();

        // Tracked load — this is a write path. One query for the whole batch.
        var products = await context.Products
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(cancellationToken);

        // Any missing product fails the whole batch before any mutation.
        if (products.Count != ids.Count)
            return false;

        foreach (var product in products)
        {
            if (product.StockQuantity < demand[product.Id])
                return false;
        }

        foreach (var product in products)
        {
            product.AdjustStock(product.StockQuantity - demand[product.Id]);
        }

        try
        {
            // Single SaveChanges = single transaction = all-or-nothing. Every product's xmin
            // token is checked in the same UPDATE batch; one stale row rolls back all lines.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }

        // Invalidate AFTER the save, same ordering rule as ReserveStock/UpdateProduct —
        // the cache must never outlive the concurrency check.
        foreach (var id in ids)
        {
            await cache.InvalidateAsync(id, cancellationToken);
        }

        return true;
    }
}
