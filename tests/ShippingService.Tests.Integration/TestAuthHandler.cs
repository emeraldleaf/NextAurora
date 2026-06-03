using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShippingService.Tests.Integration;

/// <summary>
/// Always-succeeds authentication handler registered by <see cref="ShippingApiFactory"/>. The
/// <c>NameIdentifier</c> claim carries <see cref="BuyerId"/> — the value the
/// <c>GET /api/v1/shipments/order/{orderId}</c> endpoint reads from the JWT and pushes into the
/// query's <c>RequestingBuyerId</c> for the ownership predicate. The IDOR test deliberately
/// seeds a shipment owned by a <i>different</i> buyer so the predicate filters it out at the
/// SQL level and the endpoint returns 404.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>The fixed buyer Guid stamped onto the authenticated principal.</summary>
    public static readonly Guid BuyerId = Guid.Parse("00000000-0000-0000-0000-00000000B0B0");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, BuyerId.ToString()),
            new("preferred_username", "test-buyer"),
        ];
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
