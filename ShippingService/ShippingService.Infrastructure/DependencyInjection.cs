using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShippingService.Domain.Interfaces;
using ShippingService.Infrastructure.Data;
using ShippingService.Infrastructure.Messaging;
using ShippingService.Infrastructure.Repositories;

namespace ShippingService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddShippingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ShippingDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("shipping-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<ShippingDbContext>();

        services.AddScoped<IShipmentRepository, ShipmentRepository>();

        services.AddSingleton(_ =>
            new ServiceBusClient(configuration.GetConnectionString("messaging")));

        services.AddScoped<IEventPublisher, ServiceBusEventPublisher>();
        services.AddHostedService<PaymentCompletedProcessor>();

        return services;
    }
}
