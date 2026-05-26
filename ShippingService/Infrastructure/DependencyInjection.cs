using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShippingService.Domain;
using ShippingService.Infrastructure.Data;

namespace ShippingService.Infrastructure;

/// <summary>
/// Composition root for ShippingService. Wires up PostgreSQL (shipping-db) and the
/// Wolverine-backed event publisher. There is no IShipmentRepository — handlers take
/// ShippingDbContext directly (DbContext IS Unit-of-Work; DbSet&lt;T&gt; IS Repository).
/// See CLAUDE.md "Data access: DbContext directly, no repository wrappers".
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddShippingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ShippingDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("shipping-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<ShippingDbContext>();

        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        return services;
    }
}
