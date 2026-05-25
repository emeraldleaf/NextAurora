using CatalogService.Application.Commands;
using CatalogService.Application.Interfaces;
using CatalogService.Domain.Interfaces;

namespace CatalogService.Application.Handlers;

/// <summary>
/// Updates a product's editable details (name, description, price). Cache invalidation runs
/// AFTER the DB write succeeds — invalidating before the save would create a window where the
/// cache could be repopulated with the OLD value by a concurrent read.
///
/// <para>
/// <b>IDOR contract (CLAUDE.md "Security Requirements"):</b> returns <c>false</c> on BOTH
/// "product not found" AND "seller mismatch" — the caller cannot distinguish the two. The
/// endpoint translates <c>false</c> to 404. Returning 403 on seller mismatch would leak
/// existence ("this product exists, just not yours") and let an attacker enumerate the
/// product-ID space; 404 is indistinguishable from "doesn't exist." This is the seller-
/// scope variant of the canonical anti-enumeration pattern listed in CLAUDE.md's reference
/// templates.
/// </para>
/// <para>
/// Per CLAUDE.md "Performance Rules", cache invalidation belongs in the write path, not "later"
/// or "via TTL". TTL is the safety net for the small race window between save and invalidate;
/// it does not replace this call. Invalidation only runs on the success path — a rejected
/// update has nothing to invalidate.
/// </para>
/// </summary>
public class UpdateProductHandler(IProductRepository repository, IProductCache cache)
{
    public async Task<bool> HandleAsync(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        var product = await repository.GetByIdAsync(request.ProductId, cancellationToken);
        if (product is null)
            return false;

        // Ownership guard (defense in depth — the endpoint already verifies the JWT subject
        // matches command.SellerId). This second check confirms the stored product actually
        // belongs to that seller, closing the gap where a caller pairs their own seller id with
        // someone else's product id. Returns false (NOT throws) so the endpoint maps to 404 —
        // see class summary for the anti-enumeration rationale.
        if (!string.Equals(product.SellerId, request.SellerId, StringComparison.Ordinal))
            return false;

        product.UpdateDetails(request.Name, request.Description, request.Price);
        await repository.UpdateAsync(product, cancellationToken);

        // Invalidate AFTER the save so a concurrent reader can't repopulate the cache with the
        // pre-update DTO between our invalidate and our save.
        await cache.InvalidateAsync(request.ProductId, cancellationToken);
        return true;
    }
}
