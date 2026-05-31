using NextAurora.Contracts.DTOs;

namespace CatalogService.Domain;

/// <summary>
/// Cache-aside abstraction for the read-side of the Product aggregate. Lives in Domain
/// alongside the aggregates it relates to — matches the VSA pattern in the other four
/// services (ports live next to the aggregates that need them). The implementation is in
/// <c>CatalogService.Infrastructure.Caching</c>.
///
/// <para>
/// <b>Why <c>GetOrLoadAsync</c> takes the factory:</b> the .NET 10 <c>HybridCache</c> primitive
/// owns the L1+L2 lookup, stampede protection (concurrent misses for the same key only invoke
/// the factory once), and entry storage in one method. The factory is the caller's "how to
/// produce the value if not cached" — usually a repository call followed by DTO projection.
/// Keeping the cache-aside flow inside the cache (not the handler) is what unlocks stampede
/// protection.
/// </para>
/// <para>
/// <b>Contract — what we cache:</b> <see cref="ProductDto"/>, the read-shape that
/// <c>GetProductByIdHandler</c> returns. Caching the projection rather than the EF entity
/// means no change-tracking footguns on deserialize and the cached unit matches the endpoint
/// output exactly.
/// </para>
/// <para>
/// <b>Contract — invalidation:</b> any handler that mutates a Product (update, stock adjust)
/// must call <see cref="InvalidateAsync"/> in the same unit of work. CLAUDE.md "Performance
/// Rules" — invalidation belongs in the write path, not "later" or "via TTL". TTL is the
/// safety net for the race window between write and invalidate.
/// </para>
/// </summary>
public interface IProductCache
{
    /// <summary>
    /// Cache-aside fetch with stampede protection. If the value is in cache (L1 or L2) it's
    /// returned without invoking the factory. On miss, the factory runs once even under
    /// concurrent contention; its result is cached and returned to all waiting callers.
    /// A <c>null</c> return from the factory is cached as a "negative" entry — for our
    /// system, products are server-generated GUIDs, so the "not yet exists" race window is
    /// effectively zero.
    /// </summary>
    Task<ProductDto?> GetOrLoadAsync(
        Guid productId,
        Func<CancellationToken, Task<ProductDto?>> factory,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the cached entry for this product across both cache tiers. Idempotent.
    /// Implemented via tag-based invalidation so a single call clears L1 and L2 atomically.
    /// </summary>
    Task InvalidateAsync(Guid productId, CancellationToken ct = default);
}
