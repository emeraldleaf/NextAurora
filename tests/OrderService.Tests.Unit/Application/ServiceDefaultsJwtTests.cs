using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace OrderService.Tests.Unit.Application;

/// <summary>
/// Tests for the JWT Bearer options configured in
/// <c>NextAurora.ServiceDefaults.Extensions.AddDefaultAuthentication</c> — pins the
/// security-hardened defaults (explicit signing-key validation + tight ClockSkew)
/// so a future config change can't silently weaken them.
/// </summary>
public class ServiceDefaultsJwtTests
{
    [Fact]
    public void AddServiceDefaults_WhenAuthorityConfigured_SetsExplicitSigningKeyValidation()
    {
        using var host = BuildHostWithAuthority();
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // Without this, JWT Bearer's implicit default still validates (via JWKS), but
        // making it explicit makes the security posture auditable + prevents a future
        // config refactor from accidentally disabling signature validation.
        options.TokenValidationParameters.ValidateIssuerSigningKey.Should().BeTrue();
    }

    [Fact]
    public void AddServiceDefaults_WhenAuthorityConfigured_SetsTightClockSkew()
    {
        using var host = BuildHostWithAuthority();
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // Default ClockSkew is 5 minutes — revoked/expired tokens stay accepted for
        // 5 extra minutes. The realm pins 5-MINUTE access tokens (nextaurora-realm.json
        // accessTokenLifespan: 300), so the default skew would double every token's
        // effective lifetime. We pin 30 seconds: covers inter-server clock drift
        // without giving attackers a long replay window.
        options.TokenValidationParameters.ClockSkew.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void AddServiceDefaults_WhenAuthorityConfigured_RetainsCoreValidations()
    {
        // Defense against a refactor that drops one of the foundational validations.
        // ValidateAudience/Issuer/Lifetime were already on; this test pins them.
        using var host = BuildHostWithAuthority();
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        options.TokenValidationParameters.ValidateLifetime.Should().BeTrue();
    }

    [Fact]
    public void AddServiceDefaults_WhenAuthorityUsesHttpInDevelopment_AllowsHttpMetadata()
    {
        // ARRANGE — an http authority in Development: the local Aspire Keycloak container
        // serves plain http, and this is the ONLY environment where deriving "http is fine"
        // is safe. Without this carve-out, every local run fails OIDC discovery at startup.
        using var host = BuildHostWithAuthority("http://localhost:63935", environmentName: "Development");

        // ACT — resolve the JwtBearer options AddDefaultAuthentication configured.
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // ASSERT — http metadata is permitted, so local dev works with no config override.
        options.RequireHttpsMetadata.Should().BeFalse();
    }

    [Fact]
    public void AddServiceDefaults_WhenAuthorityUsesHttpOutsideDevelopment_RequiresHttpsMetadata()
    {
        // ARRANGE — an http authority in Production. This is almost always a misconfiguration
        // (env-var typo, proxy scheme rewrite), and the SECURITY-CRITICAL behavior is failing
        // CLOSED: our default requires https metadata, and ASP.NET's own JwtBearer guard then
        // REFUSES the http authority outright — a loud InvalidOperationException instead of
        // silently fetching OIDC discovery + JWKS over plaintext, where an active MITM could
        // inject signing keys and forge tokens every service accepts.
        using var host = BuildHostWithAuthority("http://keycloak.internal", environmentName: "Production");

        // ACT — resolving the JwtBearer options triggers the framework's post-configure guard.
        var resolve = () => host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // ASSERT — the broken config fails LOUDLY at options resolution (the framework guard:
        // "The MetadataAddress or Authority must use HTTPS"). Legitimate internal-http
        // deployments must opt out explicitly (see the override test below).
        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*must use HTTPS*");
    }

    [Fact]
    public void AddServiceDefaults_WhenAuthorityUsesHttps_RequiresHttpsMetadata()
    {
        // ARRANGE — the normal production shape: an https authority.
        using var host = BuildHostWithAuthority("https://login.example.test", environmentName: "Production");

        // ACT — resolve the configured JwtBearer options.
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // ASSERT — https metadata required (trivially satisfied by an https authority; the
        // assertion pins that nothing downgrades it).
        options.RequireHttpsMetadata.Should().BeTrue();
    }

    [Fact]
    public void AddServiceDefaults_WhenExplicitOverrideFalse_AllowsHttpMetadataOutsideDevelopment()
    {
        // ARRANGE — the auditable escape hatch: Authentication:RequireHttpsMetadata=false set
        // EXPLICITLY, with an http authority in Production. This is the one sanctioned path for
        // legitimate internal-http deployments (e.g. Keycloak behind a service mesh where TLS
        // terminates at the mesh boundary) — the operator states the intent in config, where a
        // reviewer can see it, instead of the code deriving it silently.
        using var host = BuildHostWithAuthority(
            "http://keycloak.internal",
            environmentName: "Production",
            requireHttpsMetadataOverride: "false");

        // ACT — resolve the configured JwtBearer options.
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // ASSERT — the explicit override wins over the fail-closed default. (A warning is
        // logged at options resolution so the opt-out is visible in the log stream.)
        options.RequireHttpsMetadata.Should().BeFalse();
    }

    private static IHost BuildHostWithAuthority(
        string? authServerUrl = "https://example.keycloak.test",
        string environmentName = "Development",
        string? requireHttpsMetadataOverride = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = environmentName;

        // AddDefaultAuthentication branches on whether an authority is configured.
        // With one, it wires AddJwtBearer with the TokenValidationParameters we're
        // testing. Without one, it registers no-op auth and the JwtBearerOptions
        // doesn't get configured. The authority is built from the keys the Aspire
        // Keycloak.AuthServices integration actually injects (AuthServerUrl + Realm) —
        // see AddDefaultAuthentication; resolves to https://example.keycloak.test/realms/nextaurora.
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Keycloak:AuthServerUrl"] = authServerUrl,
            ["Keycloak:Realm"] = "nextaurora",
        };
        if (requireHttpsMetadataOverride is not null)
        {
            settings["Authentication:RequireHttpsMetadata"] = requireHttpsMetadataOverride;
        }

        builder.Configuration.AddInMemoryCollection(settings);

        builder.AddServiceDefaults();
        return builder.Build();
    }
}
