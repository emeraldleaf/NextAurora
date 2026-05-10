using CatalogService.Application.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Infrastructure.Caching;

/// <summary>
/// <see cref="IProductCache"/> backed by <see cref="HybridCache"/> — .NET 10's two-tier cache
/// abstraction. Replaces an earlier direct-IDistributedCache implementation:
///
/// <list type="bullet">
///   <item><b>L1 (in-process MemoryCache):</b> microseconds. Hot products served without
///         hitting the network at all.</item>
///   <item><b>L2 (distributed Redis):</b> milliseconds. Survives process restart, shared
///         across replicas, falls back here when L1 misses.</item>
///   <item><b>Stampede protection:</b> if N concurrent requests miss the same key,
///         <c>HybridCache</c> only calls the factory once — the others wait for the result.
///         Hand-rolled IDistributedCache approaches don't have this; under load you'd see
///         every concurrent miss trigger a separate DB roundtrip.</item>
///   <item><b>Tag-based invalidation:</b> each entry carries a per-product tag
///         (<c>product:{id}</c>); <see cref="InvalidateAsync"/> removes by tag, which clears
///         both L1 and L2 atomically. Without tags we'd have to remove from each layer
///         separately.</item>
/// </list>
///
/// <para>
/// <b>Key namespace.</b> <c>catalog:product:{guid}</c>. Distinct from the tag
/// (<c>product:{guid}</c>) because tags are scoped within HybridCache itself and don't need
/// the service prefix.
/// </para>
/// <para>
/// <b>TTL.</b> 5 minutes absolute on both layers. Configured via
/// <c>HybridCacheEntryOptions.Expiration</c> (overall) plus <c>LocalCacheExpiration</c>
/// (L1-specific). Both at 5min keeps the model simple — bounded staleness either way.
/// </para>
/// <para>
/// <b>Why we keep <see cref="IProductCache"/> as our seam</b> rather than letting handlers
/// depend on <see cref="HybridCache"/> directly: handlers depend on a domain-friendly
/// abstraction that hides the framework. Tests substitute <see cref="IProductCache"/>
/// without spinning up a cache instance. See CLAUDE.md.
/// </para>
/// </summary>
public sealed class HybridProductCache(HybridCache cache) : IProductCache
{
    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    private static string KeyFor(Guid productId) => $"catalog:product:{productId:N}";
    private static string TagFor(Guid productId) => $"product:{productId:N}";

    public Task<ProductDto?> GetOrLoadAsync(
        Guid productId,
        Func<CancellationToken, Task<ProductDto?>> factory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        // GetOrCreateAsync handles the full cache-aside dance: check L1 → L2 → invoke
        // factory on miss (with stampede protection) → store in L2 + L1 → return.
        // We adapt the signature: HybridCache wants ValueTask, our contract returns Task.
        var task = cache.GetOrCreateAsync(
            KeyFor(productId),
            factory: async cancel => await factory(cancel).ConfigureAwait(false),
            options: Options,
            tags: [TagFor(productId)],
            cancellationToken: ct);
        return task.AsTask();
    }

    public Task InvalidateAsync(Guid productId, CancellationToken ct = default)
        => cache.RemoveByTagAsync(TagFor(productId), ct).AsTask();
}
