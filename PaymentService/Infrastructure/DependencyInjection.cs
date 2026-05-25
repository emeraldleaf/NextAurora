using Medallion.Threading;
using Medallion.Threading.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Domain;
using PaymentService.Infrastructure.Data;
using PaymentService.Infrastructure.Gateway;

namespace PaymentService.Infrastructure;

/// <summary>
/// Composition root for PaymentService. Three concrete adapters registered against domain
/// abstractions: the EF repository, the Stripe gateway (anti-corruption layer), and the
/// Wolverine event publisher. Also wires the recovery sweeper background job + its distributed
/// lock provider.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddPaymentInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("payments-db")
            ?? throw new InvalidOperationException("Connection string 'payments-db' is required.");

        services.AddDbContext<PaymentDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddHealthChecks()
            .AddDbContextCheck<PaymentDbContext>();

        // No IPaymentRepository — handlers take PaymentDbContext directly. The outbox-atomic
        // wrapper that used to live on the repo (ExecuteInTransactionAsync) is now inline in
        // PaymentRecoveryJob.RecoverOneAsync. See CLAUDE.md "Data access: DbContext directly".

        // IPaymentGateway is the domain abstraction; StripePaymentGateway is the current
        // implementation. Swapping providers (Adyen, PayPal) means registering a different
        // implementation here — handlers don't change.
        services.AddScoped<IPaymentGateway, StripePaymentGateway>();
        services.AddScoped<IEventPublisher, WolverineEventPublisher>();

        // Distributed-lock provider for the recovery sweeper. SQL Server's sp_getapplock,
        // backed by the same connection string as the EF context — no new infrastructure.
        // The provider is a thread-safe singleton (per the library's docs); each acquire
        // opens its own short-lived session.
        services.AddSingleton<IDistributedLockProvider>(_ =>
            new SqlDistributedSynchronizationProvider(connectionString));

        services.AddOptions<PaymentRecoveryOptions>()
            .BindConfiguration("PaymentRecovery");

        services.AddSingleton(TimeProvider.System);
        services.AddHostedService<PaymentRecoveryJob>();

        return services;
    }
}
