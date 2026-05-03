using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShippingService.Domain.Interfaces;
using ShippingService.Infrastructure.Data;
using ShippingService.Infrastructure.Messaging;
using ShippingService.Infrastructure.Repositories;

namespace ShippingService.Infrastructure;

/// <summary>
/// Composition root for ShippingService's Infrastructure layer. Wires up PostgreSQL
/// (shipping-db), the EF repository, and the Wolverine-backed event publisher. Same scoped
/// lifetime convention as the other services.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddShippingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ShippingDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("shipping-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<ShippingDbContext>();

        services.AddScoped<IShipmentRepository, ShipmentRepository>();
        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        return services;
    }
}
