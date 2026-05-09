using System.Text.Json;
using CatalogService.Application.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Infrastructure.Caching;

/// <summary>
/// <see cref="IProductCache"/> backed by <see cref="IDistributedCache"/> — in dev, that resolves
/// to the Redis container Aspire spins up; in production, any IDistributedCache provider works
/// without code changes (ElastiCache, Azure Cache for Redis, in-memory for tests).
///
/// <para>
/// <b>Key namespace.</b> All keys use the <c>catalog:product:{guid}</c> prefix so multiple
/// services sharing a Redis instance don't collide and ops can <c>SCAN catalog:product:*</c>
/// without false positives.
/// </para>
/// <para>
/// <b>TTL strategy.</b> Five-minute absolute expiration — short enough that a missed
/// invalidation (network blip, process crash between DB save and cache invalidate) self-heals
/// quickly; long enough to give meaningful read amplification on hot products. We use absolute
/// rather than sliding because we want bounded staleness, not "stays cached as long as it's
/// being read."
/// </para>
/// <para>
/// <b>Serialization.</b> System.Text.Json with the DTO's default JSON shape. ProductDto is
/// flat and small (~200 bytes serialized), so we don't need MessagePack or other binary
/// format yet. If cache memory pressure becomes a real concern, switching to a binary
/// serializer is a one-file change.
/// </para>
/// <para>
/// <b>Failure mode.</b> If Redis is unreachable, IDistributedCache throws. We do NOT swallow
/// exceptions here — a hard cache failure is better than silently degrading behind a fake
/// cache. The orchestrator (Aspire health checks, k8s readiness probes in prod) should catch
/// Redis being down before traffic hits the service.
/// </para>
/// </summary>
public sealed class RedisProductCache(IDistributedCache cache) : IProductCache
{
    private static readonly DistributedCacheEntryOptions Options = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    };

    private static string KeyFor(Guid productId) => $"catalog:product:{productId:N}";

    public async Task<ProductDto?> GetAsync(Guid productId, CancellationToken ct = default)
    {
        var bytes = await cache.GetAsync(KeyFor(productId), ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;

        // Defensive: if a stale schema-incompatible entry is in cache (e.g., DTO shape changed
        // between deploys), treat as a miss rather than crashing. Caller will repopulate.
        try
        {
            return JsonSerializer.Deserialize<ProductDto>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SetAsync(ProductDto product, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(product);
        return cache.SetAsync(KeyFor(product.Id), bytes, Options, ct);
    }

    public Task InvalidateAsync(Guid productId, CancellationToken ct = default)
        => cache.RemoveAsync(KeyFor(productId), ct);
}
