using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NextAurora.Contracts.DTOs;
using NextAurora.ServiceDefaults;
using NSubstitute;
using OrderService.Domain;
using OrderService.Features;
using Xunit;

namespace OrderService.Tests.Integration;

/// <summary>
/// The Turnstile gate on POST /orders (deployed-demo bot protection). What matters here is
/// the FAIL-CLOSED contract, because a bot-gate that quietly opens is worse than none:
/// enabled + no token → 403 before the handler runs; enabled + rejected token → 403;
/// enabled + verified token → the request proceeds; disabled (the local-dev default, and
/// this factory's default config) → the filter is a no-op and every other test in this
/// suite runs untouched.
/// </summary>
public sealed class TurnstileTests : IClassFixture<OrderApiFactory>
{
    private readonly OrderApiFactory _factory;

    public TurnstileTests(OrderApiFactory factory)
    {
        _factory = factory;
    }

    private static PlaceOrderCommand ValidCommand() => new(
        BuyerId: TestAuthHandler.BuyerId,
        Currency: "USD",
        Lines: [new PlaceOrderLineItem(Guid.NewGuid(), "Test Product", 1, 9.99m)]);

    [Fact]
    public async Task PostOrders_WithoutToken_Is403_WhenTurnstileEnabled()
    {
        // ARRANGE — same booted service, Turnstile flipped on via a derived host. The
        // verifier is substituted and would APPROVE anything — proving the 403 below comes
        // from the missing-header check, not from a verification round-trip.
        var verifier = Substitute.For<ITurnstileVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        using var demo = WithTurnstile(verifier);
        var client = demo.CreateClient();

        // ACT — a fully valid, authenticated order request with no X-Turnstile-Token.
        var response = await client.PostAsJsonAsync("/api/v1/orders", ValidCommand());

        // ASSERT — refused before the handler: fail-closed means no token, no order.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await verifier.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default, default);
    }

    [Fact]
    public async Task PostOrders_WithRejectedToken_Is403()
    {
        // ARRANGE — the verifier says no (expired/replayed/bot token).
        var verifier = Substitute.For<ITurnstileVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(false);
        using var demo = WithTurnstile(verifier);
        var client = demo.CreateClient();
        client.DefaultRequestHeaders.Add(TurnstileExtensions.TokenHeader, "bot-token");

        // ACT
        var response = await client.PostAsJsonAsync("/api/v1/orders", ValidCommand());

        // ASSERT — rejected verification is a 403, and the token really was checked.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await verifier.Received(1).VerifyAsync("bot-token", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostOrders_WithVerifiedToken_ReachesTheHandler()
    {
        // ARRANGE — the verifier approves. The stubbed Catalog client (factory default)
        // validates the line, so a pass-through should produce the normal 202.
        var verifier = Substitute.For<ITurnstileVerifier>();
        verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(true);
        var productId = Guid.NewGuid();
        using var demo = WithTurnstile(verifier);
        _factory.Catalog.ValidateLinesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns([new ProductDto { Id = productId, Name = "Test Product", Price = 9.99m, Currency = "USD", StockQuantity = 5, IsAvailable = true }]);
        _factory.Catalog.ReserveLinesAsync(Arg.Any<IReadOnlyCollection<CatalogReserveLine>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var client = demo.CreateClient();
        client.DefaultRequestHeaders.Add(TurnstileExtensions.TokenHeader, "human-token");

        var command = new PlaceOrderCommand(
            BuyerId: TestAuthHandler.BuyerId,
            Currency: "USD",
            Lines: [new PlaceOrderLineItem(productId, "Test Product", 1, 9.99m)]);

        // ACT
        var response = await client.PostAsJsonAsync("/api/v1/orders", command);

        // ASSERT — the gate is a filter, not a wall: verified humans get the normal 202.
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private WebApplicationFactory<Program> WithTurnstile(ITurnstileVerifier verifier) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Turnstile:Enabled", "true");
            builder.UseSetting("Turnstile:SecretKey", "test-secret");
            builder.ConfigureTestServices(services => services.AddSingleton(verifier));
        });
}
