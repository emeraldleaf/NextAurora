using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderService.Tests.Integration;

/// <summary>
/// Always-succeeds authentication handler, registered as the default scheme by
/// <see cref="OrderApiFactory"/> in <c>ConfigureTestServices</c>. Real OrderService auth is JWT
/// Bearer against Keycloak; tests here exercise the outbox and saga handlers, not the auth path.
/// The principal's <c>NameIdentifier</c> claim is the buyer Guid the endpoints' buyer-scope check
/// compares against — tests use <see cref="BuyerId"/> in their <c>PlaceOrderCommand</c> so the
/// 403-guard passes.
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
