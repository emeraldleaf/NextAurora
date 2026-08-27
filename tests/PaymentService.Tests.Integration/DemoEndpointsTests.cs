using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using PaymentService.Endpoints;
using Xunit;

namespace PaymentService.Tests.Integration;

/// <summary>
/// The kill switch's gating contract (#208). Two invariants matter more than the happy path:
/// the endpoints must NOT exist outside DemoMode (a public deployment without the flag
/// exposes no listener controls at all), and under a stubbed transport the controls degrade
/// honestly (report Unavailable / 503) rather than pretending to pause anything. The real
/// pause→hold→revive behavior needs a live RabbitMQ listener, verified against the local
/// Aspire stack (and eventually the deployed demo) — see #208.
/// </summary>
public sealed class DemoEndpointsTests : IClassFixture<PaymentApiFactory>
{
    private readonly PaymentApiFactory _factory;

    public DemoEndpointsTests(PaymentApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DemoEndpoints_AreAbsent_WhenDemoModeIsOff()
    {
        // ARRANGE — the default factory has no DemoMode setting, i.e. a production-shaped
        // configuration. The endpoints must be unmapped, not merely forbidden: 404 makes a
        // non-demo deployment indistinguishable from one where the route never existed.
        var client = _factory.CreateClient();

        // ACT — probe the status endpoint (authenticated — the test scheme always succeeds,
        // so a 404 here can only mean "route not mapped", never "auth rejected").
        var response = await client.GetAsync(new Uri("/api/v1/demo/listener", UriKind.Relative));

        // ASSERT — the whole demo surface is gated out.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DemoEndpoints_ReportUnavailable_WhenTransportIsStubbed()
    {
        // ARRANGE — same booted service, DemoMode flipped on via a derived host (shares the
        // SQL container). The integration environment stubs RabbitMQ, so no listening agent
        // exists — the controls must say so rather than fake success.
        using var demoFactory = _factory.WithWebHostBuilder(builder => builder.UseSetting("DemoMode", "true"));
        var client = demoFactory.CreateClient();

        // ACT — read status, then try to pause the (nonexistent) listener.
        var status = await client.GetFromJsonAsync<DemoListenerStatus>(new Uri("/api/v1/demo/listener", UriKind.Relative));
        var pause = await client.PostAsync(new Uri("/api/v1/demo/listener/pause", UriKind.Relative), content: null);

        // ASSERT —
        // 1. Status endpoint answers (the gate is open) and reports the honest state.
        status.Should().NotBeNull();
        status!.Status.Should().Be("Unavailable");
        // 2. Pause refuses (503) instead of claiming to have paused a listener that isn't there.
        pause.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}
