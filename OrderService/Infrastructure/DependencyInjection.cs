using CatalogService.Api.Grpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;
using OrderService.Infrastructure.Data;

namespace OrderService.Infrastructure;

/// <summary>
/// Composition root for OrderService. Wires up SQL Server (orders-db), the EF repository, the
/// Wolverine-backed event publisher, and the gRPC client to CatalogService.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOrderInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<OrderDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("orders-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<OrderDbContext>();

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        services.AddGrpcClient<CatalogGrpc.CatalogGrpcClient>(o =>
        {
            o.Address = new Uri("https+http://catalog-service");
        });
        services.AddScoped<ICatalogClient, GrpcCatalogClient>();

        return services;
    }
}
