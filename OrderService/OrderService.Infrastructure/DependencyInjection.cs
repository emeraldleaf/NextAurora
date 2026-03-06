using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Domain.Interfaces;
using OrderService.Infrastructure.Data;
using OrderService.Infrastructure.Messaging;
using OrderService.Infrastructure.Repositories;

namespace OrderService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<OrderDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("orders-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<OrderDbContext>();

        services.AddScoped<IOrderRepository, OrderRepository>();

        // ServiceBusClient is kept for AdminEventEndpoints replay functionality.
        services.AddSingleton(_ =>
            new ServiceBusClient(configuration.GetConnectionString("messaging")));

        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        return services;
    }
}
