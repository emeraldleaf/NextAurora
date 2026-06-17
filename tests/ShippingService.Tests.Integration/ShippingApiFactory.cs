using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Wolverine;
using Xunit;

namespace ShippingService.Tests.Integration;

/// <summary>
/// Boots the real ShippingService API in-process against a throwaway Postgres container, with
/// Wolverine's external transports stubbed so the saga handlers run locally over the real DB.
///
/// <para>
/// <b>What this exercises that unit tests can't:</b> Wolverine's transactional outbox
/// (<c>PersistMessagesWithPostgresql</c>) staging the outgoing <c>ShipmentDispatchedEvent</c> in
/// the same transaction as the <c>Shipment</c> write; the saga consume-side
/// <c>PaymentCompletedHandler</c> creating a Shipment over real EF + Postgres; the IDOR-safe
/// SQL predicate on <c>GetShipmentByOrder</c> filtering at the database, not in C#; the
/// <c>xmin</c> concurrency token; and EF migrations applying against a fresh Postgres.
/// </para>
/// <para>
/// <b>Why stub the transport instead of the ASB emulator:</b> outbox-staging atomicity and
/// handler logic are what unit tests can't reach. The ASB wire path mostly exercises Microsoft's
/// emulator + Wolverine's transport adapter — the fragile last mile, not the load-bearing
/// correctness piece. See <c>docs/STATUS.md</c>.
/// </para>
/// <para>
/// <b>Why a fake "messaging" connection string:</b> <c>Program.cs</c> parses
/// <c>UseAzureServiceBus(GetConnectionString("messaging")!)</c> eagerly at registration, even
/// with external transports later disabled. The string has to be syntactically valid ASB.
/// </para>
/// </summary>
public sealed class ShippingApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        // Dispose the host BEFORE Postgres so Wolverine's durable background agents stop polling
        // the wolverine.* tables before the DB is yanked — otherwise their heartbeats hit
        // "connection refused" and crash the test host after every test passed. Swallow the
        // cancellation a slow shutdown can surface (delayed background stop isn't a correctness
        // signal). Mirrors the OrderApiFactory teardown.
        try
        {
            await base.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Intentional swallow — TaskCanceledException derives from OperationCanceledException.
        }

        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Disable Wolverine AutoProvision — against the fake ASB string it would hang trying to
        // provision topics/subscriptions at startup. DisableAllExternalWolverineTransports
        // handles routing; this handles broker-provisioning, which runs earlier.
        builder.UseSetting("Wolverine:AutoProvision", "false");

        // Pin the transport to Azure Service Bus so the ASB registration branch (which parses the
        // fake connection string) runs. App default is RabbitMQ; the transport is stubbed regardless.
        builder.UseSetting("Messaging:Transport", "azureservicebus");

        // Real Postgres for the shipping DB + Wolverine outbox tables.
        builder.UseSetting("ConnectionStrings:shipping-db", _postgres.GetConnectionString());

        // Syntactically-valid ASB connection string — parsed eagerly, never used over the wire.
        // SharedAccessKey base64-decodes to "fake-shared-key-for-testing-only". The inline
        // `gitleaks:allow` marker on the literal line is the suppressor. There is no project-level
        // gitleaks config (global [[allowlists]] needs gitleaks 8.25+, runner ships 8.24.x); the
        // inline marker is the load-bearing mechanism. See CLAUDE.md.
        builder.UseSetting(
            "ConnectionStrings:messaging",
            "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=fake;SharedAccessKey=ZmFrZS1zaGFyZWQta2V5LWZvci10ZXN0aW5nLW9ubHk="); // gitleaks:allow

        builder.ConfigureTestServices(services =>
        {
            // Always-succeeds auth so .RequireAuthorization() on the shipments group passes.
            // TestAuthHandler stamps BuyerId on the principal — the SQL predicate in
            // GetShipmentByOrder filters by that, which is what the IDOR test asserts.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Disable external ASB listeners/senders. Saga events the handler publishes route to
            // in-process stubs (still through the outbox + middleware chain).
            services.DisableAllExternalWolverineTransports();
        });
    }

    /// <summary>Opens a DI scope — callers resolve <c>ShippingDbContext</c> for direct DB setup/assertions.</summary>
    public AsyncServiceScope CreateDbScope() => Services.CreateAsyncScope();
}
