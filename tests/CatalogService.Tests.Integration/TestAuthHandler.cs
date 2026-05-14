using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CatalogService.Tests.Integration;

/// <summary>
/// Always-succeeds authentication handler, registered as the default scheme by
/// <see cref="CatalogApiFactory"/> in <c>ConfigureTestServices</c>. Real CatalogService auth is
/// JWT Bearer against Keycloak — irrelevant to what these tests exercise (caching, persistence,
/// concurrency tokens). The handler stamps a fixed authenticated principal so endpoints guarded
/// by <c>.RequireAuthorization()</c> (the product write endpoints) are reachable without
/// standing up an identity provider.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, "test-seller"),
            new("preferred_username", "test-seller"),
        ];
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
