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
/// <b>Why stub the transport instead of spinning up a real RabbitMQ broker:</b> the
/// outbox-staging guarantee (entity-write + envelope-write same transaction) and the
/// handler/saga logic are what the unit tests can't reach. The broker wire path itself mostly
/// exercises RabbitMQ + Wolverine's transport adapter — it's the fragile last mile, not the
/// load-bearing correctness piece. (A RabbitMQ Testcontainer for real-wire saga coverage is the
/// deferred follow-up — see <c>docs/STATUS.md</c>.)
/// </para>
/// <para>
/// <b>Why a fake "messaging" connection string:</b> <c>Program.cs</c> does
/// <c>GetConnectionString("messaging")!</c> then <c>UseRabbitMq(...)</c>. Even with external
/// transports disabled, the connection is parsed at registration time, so the string has to be a
/// syntactically valid AMQP URI. It's never used over the wire.
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
        // Dispose the host BEFORE the SQL container. Wolverine's DurableReceiver
        // background service polls the wolverine.* outbox tables on a heartbeat; if SQL
        // Server is torn down while the receiver is still running, every heartbeat hits
        // "connection refused" and the unhandled exceptions crash the test host AFTER
        // all tests passed. base.DisposeAsync() runs the host's StopAsync, which lets
        // Wolverine's background services exit gracefully before we yank the DB.
        //
        // Catch TaskCanceledException / OperationCanceledException during shutdown:
        // Wolverine has several durable agents (outbox dispatcher, scheduled-message
        // agent, listener heartbeats) that can outlive the host's default shutdown
        // grace period under CI's slower scheduling. When they do, the cancellation
        // propagates out through base.DisposeAsync(), xUnit catches it as a
        // "Test Class Cleanup Failure", and `dotnet test` exits non-zero — even
        // though every test passed. The tests are done by this point; a delayed
        // background-service shutdown isn't a correctness signal we want to fail
        // the build on. If we ever need to debug a real teardown bug, swap the
        // catch for a log statement.
        try
        {
            await base.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Intentional swallow — see method header. TaskCanceledException derives
            // from OperationCanceledException, so this one catch covers both.
        }

        await _sqlServer.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Disable Wolverine's .AutoProvision() in Program.cs. AutoProvision connects to the broker
        // at host startup to declare exchanges/queues; against our fake connection string it would
        // hang. DisableAllExternalWolverineTransports() below handles message routing, but
        // provisioning runs *before* ConfigureTestServices fires, so it's gated off here.
        builder.UseSetting("Wolverine:AutoProvision", "false");

        // Real SQL Server for the order DB + Wolverine outbox tables.
        builder.UseSetting("ConnectionStrings:orders-db", _sqlServer.GetConnectionString());

        // Syntactically-valid RabbitMQ (AMQP) connection string. Never used over the wire —
        // DisableAllExternalWolverineTransports() below routes messages to local stubs — but
        // Wolverine's UseRabbitMq(...) registration parses it eagerly.
        builder.UseSetting("ConnectionStrings:messaging", "amqp://guest:guest@localhost:5672");

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

            // Disable the RabbitMQ listeners + senders Wolverine registered in
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
