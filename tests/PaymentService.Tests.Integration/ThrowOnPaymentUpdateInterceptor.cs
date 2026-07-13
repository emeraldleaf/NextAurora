using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PaymentService.Domain;

namespace PaymentService.Tests.Integration;

/// <summary>
/// Test-only EF interceptor that throws when a <see cref="Payment"/> is being <b>updated</b>,
/// simulating a commit failure after the recovery sweep has already published its event. Used to
/// prove the <c>PaymentRecoveryJob</c>'s outbox atomicity: the job marks a stale Payment Failed and
/// publishes <c>PaymentFailedEvent</c>, then commits. If the publish is properly outbox-staged in the
/// recovery transaction, this forced rollback discards the staged envelope and nothing is dispatched.
/// If the publish fired inline (the Wolverine 6 constructor-<c>IMessageBus</c> trap), the event was
/// already sent before the throw and survives the rollback — a broken non-handler outbox.
///
/// <para>Throws only on <see cref="EntityState.Modified"/> entries, so seeding a Pending payment
/// (<see cref="EntityState.Added"/>) is unaffected.</para>
/// </summary>
public sealed class ThrowOnPaymentUpdateInterceptor : SaveChangesInterceptor
{
    public const string FailureMarker = "Simulated commit failure during recovery sweep (outbox-atomicity test).";

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var updatingPayment = eventData.Context?.ChangeTracker
            .Entries<Payment>()
            .Any(e => e.State == EntityState.Modified) ?? false;

        if (updatingPayment)
        {
            throw new InvalidOperationException(FailureMarker);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
