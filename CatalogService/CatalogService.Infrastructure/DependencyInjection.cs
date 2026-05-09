using CatalogService.Application.Interfaces;
using CatalogService.Domain.Interfaces;
using CatalogService.Infrastructure.Caching;
using CatalogService.Infrastructure.Data;
using CatalogService.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CatalogService.Infrastructure;

/// <summary>
/// Composition root for CatalogService's Infrastructure layer. Registers concrete
/// implementations against the abstractions defined in <c>CatalogService.Domain.Interfaces</c> —
/// the only place application code's <c>IProductRepository</c> dependency gets resolved to a
/// real <see cref="ProductRepository"/> + EF Core + PostgreSQL.
///
/// <para>
/// <b>Why a separate <c>AddCatalogInfrastructure</c> method:</b> keeps the Api project's
/// <c>Program.cs</c> short — one call sets up the entire data layer for this service. Also
/// keeps the EF Core / PostgreSQL knowledge contained here; the Api project never directly
/// references those packages.
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

        // Repository registrations: scoped lifetime so the repository and its DbContext share
        // the same scope and therefore the same DbContext instance.
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();

        // Distributed cache for product reads. The underlying IDistributedCache is registered
        // in Program.cs via AddStackExchangeRedisCache. See CLAUDE.md "Performance Rules" for
        // the cache-invalidation contract — every write handler that mutates a Product must
        // call IProductCache.InvalidateAsync in the same unit of work.
        services.AddScoped<IProductCache, RedisProductCache>();

        return services;
    }
}
