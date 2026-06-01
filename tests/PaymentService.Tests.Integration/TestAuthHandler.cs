using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PaymentService.Tests.Integration;

/// <summary>
/// Always-succeeds authentication handler, registered as the default scheme by
/// <see cref="PaymentApiFactory"/>. Real PaymentService auth is JWT Bearer against Keycloak;
/// these tests exercise the Acceptor + Gateway handlers, the transactional outbox, and the
/// <c>RowVersion</c> concurrency token — not the auth path. The endpoint just needs an
/// authenticated principal to satisfy <c>.RequireAuthorization()</c>.
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
