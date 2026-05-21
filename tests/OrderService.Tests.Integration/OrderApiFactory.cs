using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using OrderService.Domain;
using Testcontainers.MsSql;
using Wolverine;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// Boots the real OrderService API in-process against a throwaway SQL Server container, with
/// Wolverine's external transports stubbed out so the saga handlers run locally and the
/// transactional outbox is exercised against the real DB.
///
/// <para>
/// <b>What this exercises that unit tests can't:</b> Wolverine outbox staging in the same EF
/// transaction as the entity write, the FluentValidation + AutoApplyTransactions +
/// ContextPropagation middleware chain, EF migrations applying against real SQL Server, the
/// <c>RowVersion</c> concurrency token, and the saga consume-side handlers
/// (<see cref="OrderService.Application.EventHandlers.PaymentCompletedHandler"/> etc.) running
/// over an actual database.
/// </para>
/// <para>
/// <b>Why stub the transport instead of using the Azure Service Bus emulator container:</b> the
/// outbox-staging guarantee (entity-write + envelope-write same transaction) and the
/// handler/saga logic are what the unit tests can't reach. The ASB wire path itself mostly
/// exercises Microsoft's emulator + Wolverine's transport adapter — it's the fragile last mile,
/// not the load-bearing correctness piece. See <c>docs/STATUS.md</c> for the deferred follow-up.
/// </para>
/// <para>
/// <b>Why a fake "messaging" connection string:</b> <c>Program.cs</c> does
/// <c>GetConnectionString("messaging")!</c> then <c>UseAzureServiceBus(connectionString)</c>.
/// Even with external transports disabled, the parsing happens at registration time, so the
/// string has to be syntactically valid ASB. It's never used over the wire.
/// </para>
/// <para>
/// <b>Why stub <c>ICatalogClient</c>:</b> <c>PlaceOrderHandler</c> validates products + reserves
/// stock over gRPC to CatalogService. We're testing OrderService in isolation, so the stub
/// returns valid products with enough stock. Cross-service choreography is the heavier slice
/// tracked in STATUS.md.
/// </para>
/// </summary>
public sealed class OrderApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlServer =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    /// <summary>The stub injected in place of <c>GrpcCatalogClient</c>. Tests configure it.</summary>
    public ICatalogClient Catalog { get; } = Substitute.For<ICatalogClient>();

    public async Task InitializeAsync()
    {
        await _sqlServer.StartAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        await _sqlServer.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Real SQL Server for the order DB + Wolverine outbox tables.
        builder.UseSetting("ConnectionStrings:orders-db", _sqlServer.GetConnectionString());

        // Syntactically-valid Azure Service Bus connection string. Never used over the wire —
        // DisableAllExternalWolverineTransports below routes outgoing messages to local stubs —
        // but Wolverine's UseAzureServiceBus(...) registration parses it eagerly.
        builder.UseSetting(
            "ConnectionStrings:messaging",
            "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=fake;SharedAccessKey=ZmFrZS1zaGFyZWQta2V5LWZvci10ZXN0aW5nLW9ubHk=");

        builder.ConfigureTestServices(services =>
        {
            // Replace whatever auth ServiceDefaults registered (nothing — no Authority is
            // configured) with an always-succeeds scheme so endpoint-level
            // .RequireAuthorization() and the per-endpoint buyer-scope check both pass.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Stub the gRPC catalog client. PlaceOrderHandler validates + reserves stock through
            // this; the stub keeps OrderService boot-able without standing up CatalogService.
            services.AddSingleton<ICatalogClient>(Catalog);

            // Disable the Azure Service Bus listeners + senders Wolverine registered in
            // Program.cs. Outgoing messages route to in-process stubs, which still flow through
            // Wolverine's middleware chain (FluentValidation, AutoApplyTransactions, the
            // durable outbox, ContextPropagation) — so the outbox-staging guarantee is what
            // we actually test, not the wire.
            services.DisableAllExternalWolverineTransports();
        });
    }

    /// <summary>
    /// Opens a DI scope — callers resolve <c>OrderDbContext</c> from it for test
    /// setup/teardown that needs to touch the database directly.
    /// </summary>
    public AsyncServiceScope CreateDbScope() => Services.CreateAsyncScope();
}
