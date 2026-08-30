using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OrderService.Domain;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// The real-wire variant of <see cref="OrderApiFactory"/>: the same in-process OrderService API
/// against a throwaway SQL Server container, but with Wolverine's RabbitMQ transport LIVE against
/// a throwaway RabbitMQ container instead of stubbed. <c>AutoProvision</c> stays on (the
/// <c>Program.cs</c> default), so the app declares its own exchanges, queues and bindings against
/// the broker at startup — exactly what happens on the VPS.
///
/// <para>
/// <b>What this reaches that the stubbed factory can't:</b> the listener path. A durable inbox
/// (<c>UseDurableInboxOnAllListeners()</c>) only guards messages that arrive through a real
/// listening endpoint; with transports stubbed, <c>PublishMessageAndWaitAsync</c> hands the
/// message straight to the local pipeline and the inbox table is never consulted. Tests that
/// need to prove what happens when the broker redelivers (a duplicate envelope id) have to
/// come in over AMQP.
/// </para>
/// <para>
/// <b>Why a second factory rather than a flag on the first:</b> the stubbed factory is shared by
/// every saga test in the class fixture, and the wire path adds a container start plus broker
/// provisioning to its cost. Keeping them separate means the twenty-odd stubbed tests stay
/// fast and the wire tests stay explicit. This is the first slice of the deferred
/// "RabbitMQ Testcontainer" follow-up in docs/dev-loop.md Gap 1.
/// </para>
/// </summary>
public sealed class OrderApiRabbitFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlServer =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    // Same image family the deployment runs, so the broker under test is the broker in production.
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    /// <summary>The stub injected in place of <c>GrpcCatalogClient</c>. Tests configure it.</summary>
    public ICatalogClient Catalog { get; } = Substitute.For<ICatalogClient>();

    /// <summary>Every log entry the host emits, for asserting on infrastructure-only behavior.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>AMQP URI of the live broker, for tests that publish raw messages onto a queue.</summary>
    public string AmqpConnectionString => _rabbit.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sqlServer.StartAsync(), _rabbit.StartAsync());
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        // Host first, containers second, and swallow shutdown cancellations — same reasoning as
        // OrderApiFactory: Wolverine's durable agents poll the outbox tables and the broker on a
        // heartbeat, and a delayed background-service shutdown after every test has passed is not
        // a signal worth failing the build on.
        try
        {
            await base.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Intentional swallow — see method header.
        }

        await _sqlServer.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Real SQL Server for the order DB + the wolverine.* inbox/outbox tables.
        builder.UseSetting("ConnectionStrings:orders-db", _sqlServer.GetConnectionString());

        // Real broker. Program.cs parses this with UseRabbitMq(...) and, with AutoProvision left at
        // its default (true), declares the fanout exchanges + this service's queues against it.
        builder.UseSetting("ConnectionStrings:messaging", _rabbit.GetConnectionString());

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.AddSingleton<ICatalogClient>(Catalog);

            // Capture everything, below the host's configured minimum level — the inbox rejection
            // is the one thing under test that leaves no other trace.
            services.AddLogging(logging =>
            {
                logging.AddProvider(Logs);
                logging.AddFilter<CapturingLoggerProvider>(category: null, LogLevel.Trace);
            });

            // NOTE: no DisableAllExternalWolverineTransports() here. That is the whole point.
        });
    }

    /// <summary>
    /// Opens a DI scope — callers resolve <c>OrderDbContext</c> from it for test
    /// setup/teardown that needs to touch the database directly.
    /// </summary>
    public AsyncServiceScope CreateDbScope() => Services.CreateAsyncScope();
}
