using CatalogService.Grpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain;
using OrderService.Features;
using OrderService.Infrastructure.Data;

namespace OrderService.Infrastructure;

/// <summary>
/// Composition root for OrderService. Wires up SQL Server (orders-db) and the gRPC client to
/// CatalogService. Handlers publish events through the method-injected <c>IMessageContext</c>
/// (enlisted in the outbox transaction — see PlaceOrderHandler), so there is no IEventPublisher
/// shim here. There is no IOrderRepository —
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

        // gRPC needs HTTP/2. The Aspire `https+http://` service-discovery scheme can't be
        // consumed directly by Grpc.Net.Client's GrpcChannel (it has no Balancer resolver for
        // that scheme — `.AddServiceDiscovery()` only wires the HttpClient handler, not the
        // channel), so resolve the concrete endpoint Aspire injects and hand the channel a plain
        // https:// (or http://) address it understands. Prefer https: catalog serves HTTP/2 via
        // TLS+ALPN there (the dev cert is trusted locally); its cleartext endpoint is HTTP/1.1
        // only, which gRPC can't use. See CLAUDE.md + docs/architecture.md.
        services.AddGrpcClient<CatalogGrpc.CatalogGrpcClient>(o =>
        {
            // Fail fast on missing config rather than falling back to "https+http://catalog-service":
            // that scheme is exactly what GrpcChannel can't resolve, so a fallback would only
            // reproduce the cryptic "No address resolver" error at first call. A clear message names
            // the missing Aspire service-discovery key. (Integration tests stub ICatalogClient, so
            // this lambda never runs there — it only fires under real Aspire wiring.)
            o.Address = new Uri(
                configuration["services:catalog-service:https:0"]
                ?? configuration["services:catalog-service:http:0"]
                ?? throw new InvalidOperationException(
                    "CatalogService gRPC endpoint not configured. Expected Aspire service-discovery key "
                    + "'services:catalog-service:https:0' (or ':http:0') — check the OrderService WithReference(catalogService) wiring in AppHost.cs."));
        });
        services.AddScoped<ICatalogClient, GrpcCatalogClient>();

        services.AddScoped<GetOrderByIdHandler>();
        services.AddScoped<GetOrdersByBuyerHandler>();

        return services;
    }
}
