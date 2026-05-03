using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Domain.Interfaces;
using PaymentService.Infrastructure.Data;
using PaymentService.Infrastructure.Gateway;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.Repositories;

namespace PaymentService.Infrastructure;

/// <summary>
/// Composition root for PaymentService's Infrastructure layer. Three concrete adapters are
/// registered against domain abstractions: the EF repository, the Stripe gateway (anti-corruption
/// layer — see <see cref="StripePaymentGateway"/>), and the Wolverine event publisher.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddPaymentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PaymentDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("payments-db")));

        services.AddHealthChecks()
            .AddDbContextCheck<PaymentDbContext>();

        services.AddScoped<IPaymentRepository, PaymentRepository>();

        // IPaymentGateway is the domain abstraction; StripePaymentGateway is the current
        // implementation. Swapping providers (Adyen, PayPal) means registering a different
        // implementation here — handlers don't change.
        services.AddScoped<IPaymentGateway, StripePaymentGateway>();
        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        return services;
    }
}
