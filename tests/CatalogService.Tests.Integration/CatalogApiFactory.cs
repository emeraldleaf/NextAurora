using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

namespace CatalogService.Tests.Integration;

/// <summary>
/// Boots the real CatalogService API in-process against throwaway Docker containers:
/// a PostgreSQL instance (the catalog DB) and a Redis instance (the HybridCache L2 tier).
///
/// <para>
/// <b>What this exercises that unit tests can't:</b> EF Core migrations applying against a real
/// Postgres, the actual <c>HybridProductCache</c> over a real Redis, the <c>xmin</c> concurrency
/// token, Wolverine command/query dispatch, and the DI composition root from
/// <c>CatalogService/Program.cs</c> end to end. Unit tests substitute all of that.
/// </para>
/// <para>
/// <b>Lifecycle:</b> implements xunit's <see cref="IAsyncLifetime"/> so the containers start once
/// per test class (via <c>IClassFixture</c>) and dispose after. Container teardown lives in the
/// <see cref="WebApplicationFactory{TEntryPoint}.DisposeAsync"/> override (the method the
/// <c>CA2213</c> analyzer recognizes as <i>the</i> async-dispose method); xunit's
/// <c>IAsyncLifetime.DisposeAsync</c> returns <c>Task</c> and is implemented explicitly to route
/// into that same override.
/// </para>
/// <para>
/// <b>Connection-string injection:</b> CatalogService reads <c>ConnectionStrings:catalog-db</c>
/// and <c>ConnectionStrings:cache</c>. In production Aspire injects those; here we override them
/// with the Testcontainers-assigned host+port via <see cref="IWebHostBuilder.UseSetting"/>.
/// No <c>Authentication:Authority</c> is configured, so ServiceDefaults registers no JWT scheme —
/// we add <see cref="TestAuthHandler"/> as the default scheme instead.
/// </para>
/// </summary>
public sealed class CatalogApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    private readonly RedisContainer _redis =
        new RedisBuilder("redis:7-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
    }

    // xunit's IAsyncLifetime wants a Task-returning DisposeAsync; WebApplicationFactory<T>
    // already exposes a ValueTask one. Implement the interface method explicitly and route it
    // into the override below so there's a single teardown path.
    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    // The async-dispose method CA2213 recognizes — container teardown belongs here, then
    // chain into the base factory teardown.
    public override async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Point the app at the throwaway containers. WebApplicationFactory defaults the
        // environment to "Development", so Program.cs's IsDevelopment() block runs —
        // which is what applies the EF migrations on startup. That's intentional: a clean
        // migrate against a fresh Postgres is one of the things we want covered.
        builder.UseSetting("ConnectionStrings:catalog-db", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:cache", _redis.GetConnectionString());

        builder.ConfigureTestServices(services =>
        {
            // Replace whatever auth ServiceDefaults registered (nothing, since no Authority is
            // configured) with an always-succeeds scheme so .RequireAuthorization() endpoints
            // are reachable. Registered last, so its default-scheme wins.
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    /// <summary>
    /// Opens a DI scope — callers resolve <c>CatalogDbContext</c> from it for test setup/teardown
    /// that needs to touch the database directly (seeding rows, or asserting on raw DB state
    /// independently of the cache).
    /// </summary>
    public AsyncServiceScope CreateDbScope() => Services.CreateAsyncScope();
}
