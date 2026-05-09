using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Interfaces;

/// <summary>
/// Cache-aside abstraction for the read-side of the Product aggregate. Lives in Application
/// (not Domain) because caching is an infrastructure-policy concern, not a business invariant.
/// The implementation is in <c>CatalogService.Infrastructure.Caching</c>.
///
/// <para>
/// <b>Contract — what we cache:</b> <see cref="ProductDto"/>, the read-shape that
/// <see cref="Queries.GetProductByIdQuery"/> returns. We cache the projection, not the EF
/// entity. Reasons:
/// </para>
/// <list type="bullet">
///   <item><b>Immutability.</b> DTOs are pure data — no change tracking, no concurrency tokens
///         to misinterpret on deserialize. Caching a tracked entity is a footgun.</item>
///   <item><b>Right unit.</b> The endpoint returns a DTO. If we cache at the entity level, every
///         hit still has to project to DTO, and we've gained nothing on materialization cost.</item>
///   <item><b>Stable shape.</b> If the entity grows columns we don't expose, we don't pay
///         serialization cost for them in cache.</item>
/// </list>
///
/// <para>
/// <b>Contract — invalidation:</b> any handler that mutates a Product (update, stock adjust,
/// future delete) must call <see cref="InvalidateAsync"/> in the same unit of work. CLAUDE.md
/// "Performance Rules" — invalidation belongs in the write path, not "later" or "via TTL".
/// TTL is the safety net for the race window between write and invalidate; it does not replace
/// invalidation.
/// </para>
/// </summary>
public interface IProductCache
{
    /// <summary>
    /// Returns the cached DTO if present, otherwise null. Null means "cache miss" — the caller
    /// should fall through to the repository and then call <see cref="SetAsync"/> with the result.
    /// </summary>
    Task<ProductDto?> GetAsync(Guid productId, CancellationToken ct = default);

    /// <summary>
    /// Stores the DTO in the cache with the implementation's TTL. Idempotent — calling twice
    /// with the same DTO is fine.
    /// </summary>
    Task SetAsync(ProductDto product, CancellationToken ct = default);

    /// <summary>
    /// Removes the cached entry for this product. Call this in any handler that mutates the
    /// product (UpdateDetails, AdjustStock, future delete). Idempotent — safe to call when no
    /// entry exists.
    /// </summary>
    Task InvalidateAsync(Guid productId, CancellationToken ct = default);
}
