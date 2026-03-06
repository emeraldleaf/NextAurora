using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Domain.Interfaces;
using PaymentService.Infrastructure.Data;
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

        // ServiceBusClient is kept for AdminEventEndpoints replay functionality.
        services.AddSingleton(_ =>
            new ServiceBusClient(configuration.GetConnectionString("messaging")));

        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        return services;
    }
}
