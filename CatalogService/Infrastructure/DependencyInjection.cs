using CatalogService.Domain;
using CatalogService.Features;
using CatalogService.Infrastructure.Caching;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CatalogService.Infrastructure;

/// <summary>
/// Composition root for CatalogService. Wires up Postgres (catalog-db), the
/// HybridCache-backed product cache, and the EF Core DbContext that handlers consume
/// directly. There is no <c>IProductRepository</c> / <c>IProductReadStore</c> — handlers
/// take <see cref="CatalogDbContext"/> directly (DbContext IS Unit-of-Work, DbSet&lt;T&gt;
/// IS Repository). See CLAUDE.md "Data access: DbContext directly, no repository wrappers".
///
/// <para>
/// <b>Query-handler registration.</b> The read handlers are registered as scoped services
/// so integration tests can resolve them directly to assert the EF projection SQL.
/// Wolverine auto-discovers handlers for <c>IMessageBus</c> dispatch via reflection but
/// does not register them in <c>IServiceCollection</c>; resolving the type directly via
/// <c>GetRequiredService&lt;T&gt;()</c> requires the explicit <c>AddScoped</c> below. HTTP
/// endpoints and the gRPC service route through <c>IMessageBus</c>, so they don't depend
/// on this registration in prod. See CLAUDE.md "Communication Patterns → Wolverine
/// handler discovery is NOT DI registration".
/// </para>
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // DbContext registered as scoped (default). Each HTTP request / Wolverine message
        // dispatch gets its own instance. DbContext isn't thread-safe so one-per-scope avoids
        // accidental sharing, the change tracker stays small (only entities loaded during this
        // request), and connection pooling means the underlying DB connection is still reused.
        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("catalog-db")));

        // Health check that verifies the DB is reachable. Surfaces in Aspire's dashboard and
        // any orchestrator probing /health.
        services.AddHealthChecks()
            .AddDbContextCheck<CatalogDbContext>();

        // Two-tier cache for product reads (L1 MemoryCache + L2 Redis via IDistributedCache,
        // managed together by HybridCache). HybridCache itself is registered in Program.cs via
        // AddHybridCache. See CLAUDE.md "Performance Rules" for the cache-invalidation
        // contract — every write handler that mutates a Product must call
        // IProductCache.InvalidateAsync in the same unit of work.
        services.AddScoped<IProductCache, HybridProductCache>();

        // Query-handler DI registration so integration tests can resolve handlers directly.
        // See class summary; CLAUDE.md "Communication Patterns → Wolverine handler discovery
        // is NOT DI registration".
        services.AddScoped<GetProductByIdHandler>();
        services.AddScoped<GetAllProductsHandler>();
        services.AddScoped<SearchProductsHandler>();

        return services;
    }
}
