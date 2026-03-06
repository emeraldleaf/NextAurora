using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Domain.Interfaces;
using PaymentService.Infrastructure.Data;
using PaymentService.Infrastructure.EventLog;
using PaymentService.Infrastructure.Gateway;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.Repositories;

namespace PaymentService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPaymentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PaymentDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("payments-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<PaymentDbContext>();

        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IPaymentGateway, StripePaymentGateway>();

        services.AddSingleton(_ =>
            new ServiceBusClient(configuration.GetConnectionString("messaging")));

        services.AddScoped<ServiceBusEventPublisher>();
        services.AddScoped<IEventPublisher, LoggingEventPublisher>();
        services.AddHostedService<OrderPlacedProcessor>();

        return services;
    }
}
