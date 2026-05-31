using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PaymentService.Domain;
using Testcontainers.MsSql;
using Wolverine;
using Xunit;

namespace PaymentService.Tests.Integration;

/// <summary>
/// Boots the real PaymentService API in-process against a throwaway SQL Server container, with
/// Wolverine's external transports stubbed so the Acceptor + Gateway handlers and the
/// transactional outbox run locally against a real DB.
///
/// <para>
/// <b>What this exercises that unit tests can't:</b> the split <c>ProcessPaymentHandler</c>
/// (Acceptor) → <c>PaymentProcessingRequested</c> → <c>PaymentProcessingRequestedHandler</c>
/// (Gateway) flow running over Wolverine's local queue + middleware chain (FluentValidation,
/// AutoApplyTransactions, ContextPropagation), the outbox staging the terminal event in the same
/// EF transaction as the entity write, EF migrations applying to real SQL Server, the
/// <c>RowVersion</c> concurrency token, and the OrderId-uniqueness idempotency guard.
/// </para>
/// <para>
/// <b>Why stub the transport instead of the ASB emulator:</b> the outbox-staging guarantee and
/// the handler logic are what unit tests can't reach. The internal <c>PaymentProcessingRequested</c>
/// message has no external routing, so it stays on the in-process local queue and is fully
/// exercised here; only the outbound <c>PaymentCompletedEvent</c>/<c>PaymentFailedEvent</c> wire
/// hop is stubbed. See <c>docs/STATUS.md</c>.
/// </para>
/// <para>
/// <b>Why a fake "messaging" connection string:</b> <c>Program.cs</c> calls
/// <c>UseAzureServiceBus(GetConnectionString("messaging")!)</c>, parsed eagerly at registration
/// even with external transports disabled — so it must be syntactically valid ASB.
/// </para>
/// <para>
/// <b>Why stub <c>IPaymentGateway</c>:</b> the Gateway handler calls the Stripe gateway. Tests
/// configure the stub to return success or failure to drive the Completed / Failed branches
/// without touching a real payment provider.
/// </para>
/// </summary>
public sealed class PaymentApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _sqlServer =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    /// <summary>The stub injected in place of <c>StripePaymentGateway</c>. Tests configure it.</summary>
    public IPaymentGateway Gateway { get; } = Substitute.For<IPaymentGateway>();

    public async Task InitializeAsync()
    {
        await _sqlServer.StartAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public override async ValueTask DisposeAsync()
    {
        // Dispose the host BEFORE the SQL container so Wolverine's durable background agents
        // (outbox dispatcher, recovery sweeper) stop polling the wolverine.* tables before the
        // DB is torn down — otherwise their heartbeats hit "connection refused" and crash the
        // test host AFTER all tests passed. Swallow the cancellation that a slow shutdown can
        // surface (a delayed background-service stop isn't a correctness signal). See the
        // matching note in OrderApiFactory.
        try
        {
            await base.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Intentional swallow — TaskCanceledException derives from OperationCanceledException.
        }

        await _sqlServer.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Disable Wolverine AutoProvision — against the fake ASB string it would hang trying to
        // provision topics/subscriptions at startup. DisableAllExternalWolverineTransports below
        // handles routing; this handles the broker-provisioning that runs earlier.
        builder.UseSetting("Wolverine:AutoProvision", "false");

        // Real SQL Server for the payments DB + Wolverine outbox tables.
        builder.UseSetting("ConnectionStrings:payments-db", _sqlServer.GetConnectionString());

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
            // Always-succeeds auth so .RequireAuthorization() on /payments/process passes.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // Stub the Stripe gateway — tests drive Completed/Failed via the configured result.
            services.AddSingleton<IPaymentGateway>(Gateway);

            // Disable external ASB listeners/senders. Outgoing events route to in-process stubs
            // (still through the outbox + middleware chain); the internal PaymentProcessingRequested
            // message stays on the local queue and runs end-to-end.
            services.DisableAllExternalWolverineTransports();
        });
    }

    /// <summary>Opens a DI scope — callers resolve <c>PaymentDbContext</c> for direct DB setup/assertions.</summary>
    public AsyncServiceScope CreateDbScope() => Services.CreateAsyncScope();
}
