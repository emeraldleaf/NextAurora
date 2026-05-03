using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Interfaces;
using OrderService.Infrastructure.Data;
using OrderService.Infrastructure.Messaging;
using OrderService.Infrastructure.Repositories;

namespace OrderService.Infrastructure;

/// <summary>
/// Composition root for OrderService's Infrastructure layer. Wires up SQL Server (orders-db),
/// the EF repository, and the Wolverine-backed event publisher.
///
/// <para>
/// Same scoped-lifetime story as the other services: one DbContext per request/message dispatch,
/// repositories share that scope, event publisher is also scoped so it can participate in the
/// same transaction as the repo's <c>SaveChanges</c> when Wolverine's transactional outbox
/// wraps the handler.
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

        services.AddScoped<IOrderRepository, OrderRepository>();

        // IEventPublisher is the domain abstraction; WolverineEventPublisher is the
        // Wolverine-backed implementation. Application handlers depend on the abstraction.
        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        return services;
    }
}
