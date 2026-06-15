using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderService.Domain;

namespace OrderService.Tests.Integration;

/// <summary>
/// Test-only EF interceptor that throws when an <see cref="Order"/> is being committed, simulating
/// a post-publish commit failure. Used to prove transactional-outbox atomicity: the PlaceOrder
/// handler publishes <c>OrderPlacedEvent</c> and then calls <c>SaveChangesAsync</c>. If the publish
/// is properly outbox-staged in the handler's transaction, this forced rollback discards the staged
/// envelope and the event is never dispatched. If the publish fired *inline* (not enlisted — the
/// Wolverine 6 constructor-<c>IMessageBus</c> trap), the event was already sent before the throw and
/// survives the rollback — a broken outbox.
///
/// <para>Scoped to Added <see cref="Order"/> entities so Wolverine's own bookkeeping saves on the
/// same context aren't affected.</para>
/// </summary>
public sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
{
    public const string FailureMarker = "Simulated commit failure after publish (outbox-atomicity test).";

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var committingNewOrder = eventData.Context?.ChangeTracker
            .Entries<Order>()
            .Any(e => e.State == EntityState.Added) ?? false;

        if (committingNewOrder)
        {
            // DbUpdateException (not InvalidOperationException) so it models a real infrastructure
            // commit failure — GlobalExceptionHandler maps it to 500, distinct from the 409 it gives
            // business conflicts. That lets the test assert the failure happened at this commit, not
            // at an earlier business/validation step.
            throw new DbUpdateException(FailureMarker);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
