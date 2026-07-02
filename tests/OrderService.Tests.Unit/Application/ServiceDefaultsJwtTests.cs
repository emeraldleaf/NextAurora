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
        // 5 extra minutes, material on typical 15-minute access-token lifetimes. We
        // pin 30 seconds: covers inter-server clock drift without giving attackers a
        // long replay window.
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
    public void AddServiceDefaults_WhenAuthorityUsesHttp_AllowsHttpMetadataForLocalKeycloak()
    {
        using var host = BuildHostWithAuthority("http://localhost:63935", environmentName: "Production");
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.RequireHttpsMetadata.Should().BeFalse();
    }

    [Fact]
    public void AddServiceDefaults_WhenAuthorityUsesHttps_RequiresHttpsMetadata()
    {
        using var host = BuildHostWithAuthority("https://login.example.test", environmentName: "Production");
        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        options.RequireHttpsMetadata.Should().BeTrue();
    }

    private static IHost BuildHostWithAuthority(string? authServerUrl = "https://example.keycloak.test", string environmentName = "Development")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = environmentName;

        // AddDefaultAuthentication branches on whether an authority is configured.
        // With one, it wires AddJwtBearer with the TokenValidationParameters we're
        // testing. Without one, it registers no-op auth and the JwtBearerOptions
        // doesn't get configured. The authority is built from the keys the Aspire
        // Keycloak.AuthServices integration actually injects (AuthServerUrl + Realm) —
        // see AddDefaultAuthentication; resolves to https://example.keycloak.test/realms/nextaurora.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Keycloak:AuthServerUrl"] = authServerUrl,
            ["Keycloak:Realm"] = "nextaurora",
        });

        builder.AddServiceDefaults();
        return builder.Build();
    }
}
