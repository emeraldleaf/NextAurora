using CatalogService.Domain;
using CatalogService.Infrastructure.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Features;

/// <summary>
/// "Update product" vertical slice. Mutates editable fields (name, description, price) on a
/// product owned by the calling seller. Handler takes <c>CatalogDbContext</c> directly — no
/// <c>IProductRepository</c> wrapper (see CLAUDE.md "Data access: DbContext directly").
///
/// <para>
/// <b>IDOR contract (CLAUDE.md "Security Requirements"):</b> returns <c>false</c> on BOTH
/// "product not found" AND "seller mismatch" — the caller cannot distinguish the two. The
/// endpoint translates <c>false</c> to 404. Returning 403 on seller mismatch would leak
/// existence ("this product exists, just not yours") and let an attacker enumerate the
/// product-ID space; 404 is indistinguishable from "doesn't exist."
/// </para>
/// <para>
/// <b>Cache invalidation ordering (CLAUDE.md "Performance Rules"):</b> invalidate runs AFTER
/// <c>SaveChanges</c> succeeds. Invalidating first would create a window where a concurrent
/// read could repopulate the cache with the OLD value between our invalidate and our save.
/// Invalidation only fires on the success path — a rejected update has nothing to invalidate.
/// </para>
/// </summary>
public record UpdateProductCommand(
    Guid ProductId,
    string SellerId,
    string Name,
    string Description,
    decimal Price);

public class UpdateProductCommandValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.SellerId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Price).GreaterThan(0);
    }
}

public class UpdateProductHandler(CatalogDbContext context, IProductCache cache)
{
    public async Task<bool> HandleAsync(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await context.Products
            .Include(p => p.Category)
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken);

        if (product is null)
            return false;

        // Ownership guard — defense in depth. The endpoint already enforces JWT-sub ==
        // command.SellerId; this second check confirms the stored row actually belongs to that
        // seller, closing the gap where a caller pairs their own seller id with someone else's
        // product id. Returns false (NOT throws) so the endpoint maps to 404 per the anti-
        // enumeration rationale above.
        if (!string.Equals(product.SellerId, request.SellerId, StringComparison.Ordinal))
            return false;

        product.UpdateDetails(request.Name, request.Description, request.Price);
        await context.SaveChangesAsync(cancellationToken);

        // Invalidate AFTER the save so a concurrent reader can't repopulate the cache with the
        // pre-update DTO between our invalidate and our save.
        await cache.InvalidateAsync(request.ProductId, cancellationToken);
        return true;
    }
}
