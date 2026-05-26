using CatalogService.Grpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Infrastructure.Data;

namespace OrderService.Infrastructure;

/// <summary>
/// Composition root for OrderService. Wires up SQL Server (orders-db), the Wolverine-backed
/// event publisher, and the gRPC client to CatalogService. There is no IOrderRepository —
/// handlers take OrderDbContext directly (DbContext IS Unit-of-Work; DbSet&lt;T&gt; IS
/// Repository). See CLAUDE.md "Data access: DbContext directly, no repository wrappers".
///
/// <para>
/// <b>Query-handler registration.</b> The read handlers are registered as scoped services
/// so they can be resolved by integration tests (which exercise the EF projection SQL
/// against real SQL Server). Wolverine auto-discovers handlers for *message dispatch* via
/// reflection, but does not register them as DI services; resolving the type directly
/// requires the explicit AddScoped below. HTTP endpoints route through <c>IMessageBus</c>
/// (Wolverine's dispatcher), so they don't depend on this registration in prod.
/// </para>
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddOrderInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<OrderDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("orders-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<OrderDbContext>();

        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        services.AddGrpcClient<CatalogGrpc.CatalogGrpcClient>(o =>
        {
            o.Address = new Uri("https+http://catalog-service");
        });
        services.AddScoped<ICatalogClient, GrpcCatalogClient>();

        services.AddScoped<GetOrderByIdHandler>();
        services.AddScoped<GetOrdersByBuyerHandler>();

        return services;
    }
}
