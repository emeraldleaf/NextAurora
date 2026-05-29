using AwesomeAssertions;
using NetArchTest.Rules;
using Xunit;

namespace NextAurora.ArchitectureTests;

/// <summary>
/// Enforces Clean Architecture's <b>dependency rule</b> deterministically inside NextAurora's
/// single-project VSA shape — <i>without</i> the 4-project split. Each service's <c>Domain</c>
/// namespace must depend on nothing infrastructural (EF Core, the messaging transport, ASP.NET,
/// DB drivers, caching) and must not reach into its own <c>Infrastructure</c> or <c>Features</c>
/// (Domain is the inner circle — outer rings depend on it, never the reverse).
///
/// <para>
/// This is the <b>"architecture tests" rung</b> of the enforcement spectrum
/// <c>convention → architecture tests → project split</c> documented in CLAUDE.md
/// "Promotion signal" and <c>docs/vsa-vs-clean-architecture.md</c>. It gives the *same* boundary
/// the 4-project layout would enforce via project references — but in a single project, at the
/// namespace level, failing CI on violation. NetArchTest inspects compiled IL dependencies, so
/// XML-doc comments that merely mention "Infrastructure" or "Wolverine" don't count — only real
/// type references do.
/// </para>
/// <para>
/// NotificationService is intentionally absent: it's the "no aggregate" service (stateless
/// event-to-email pump) with no <c>Domain</c> folder, so there's no domain boundary to assert.
/// </para>
/// </summary>
public class DependencyRuleTests
{
    // Infrastructural namespaces a domain layer must never reach into. NetArchTest matches by
    // prefix, so "Microsoft.EntityFrameworkCore" also covers its sub-namespaces.
    private static readonly string[] ForbiddenInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Wolverine",
        "Dapper",
        "Npgsql",
        "Microsoft.Data.SqlClient",
        "Microsoft.Extensions.Caching",
    ];

    [Theory]
    [InlineData(typeof(CatalogService.Domain.Product), "CatalogService")]
    [InlineData(typeof(OrderService.Domain.Order), "OrderService")]
    [InlineData(typeof(PaymentService.Domain.Payment), "PaymentService")]
    [InlineData(typeof(ShippingService.Domain.Shipment), "ShippingService")]
    public void Domain_depends_on_nothing_infrastructural(Type domainAnchor, string service)
    {
        // ARRANGE — the anchor type pins the service's assembly; we scope the rule to that
        // service's Domain namespace. The forbidden set is the shared infrastructural concerns
        // PLUS the service's own Infrastructure + Features namespaces — Domain is the inner
        // circle, so a Domain→Infrastructure or Domain→Features reference inverts the dependency
        // rule. Justified Domain dependencies (NextAurora.Contracts DTOs/events, BCL) are not
        // in the list and stay allowed.
        var forbidden = ForbiddenInDomain
            .Append($"{service}.Infrastructure")
            .Append($"{service}.Features")
            .ToArray();

        // ACT — assert no type in {service}.Domain has a compiled dependency on any forbidden
        // namespace. This is the same boundary a 4-project split enforces via project
        // references, expressed as a CI-gated test in a single-project VSA codebase.
        var result = Types.InAssembly(domainAnchor.Assembly)
            .That().ResideInNamespace($"{service}.Domain")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        // ASSERT — green codifies the decoupled state; a failure names the offending Domain
        // types so the inversion is obvious in CI output.
        result.IsSuccessful.Should().BeTrue(
            because: $"{service}.Domain must stay free of infrastructure — offending types: " +
                     string.Join(", ", result.FailingTypeNames ?? []));
    }
}
