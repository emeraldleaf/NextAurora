namespace PaymentService.Domain;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default);
    Task AddAsync(Payment payment, CancellationToken ct = default);
    Task UpdateAsync(Payment payment, CancellationToken ct = default);

    /// <summary>
    /// Returns IDs of payments still in <see cref="PaymentStatus.Pending"/> with
    /// <c>CreatedAt &lt; olderThan</c>. Used by <c>PaymentRecoveryJob</c> to find candidates
    /// for sweep recovery. Returns IDs (not entities) on purpose: the sweeper loads each one
    /// in a fresh tracked query before mutating so the RowVersion concurrency token protects
    /// against double-handling when more than one instance races past the distributed lock.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetStalePendingPaymentIdsAsync(DateTime olderThan, CancellationToken ct = default);

    /// <summary>
    /// Runs <paramref name="work"/> inside a single EF Core transaction on the underlying
    /// <c>PaymentDbContext</c>, commits on success, rolls back on any exception. Wolverine's
    /// <c>UseEntityFrameworkCoreTransactions()</c> wiring detects the ambient EF transaction so
    /// any <c>IMessageBus.PublishAsync</c> call inside <paramref name="work"/> stages the outbox
    /// row in the same transaction — entity write and outbox row commit atomically.
    ///
    /// <para>
    /// Needed by <c>PaymentRecoveryJob</c>, which runs outside Wolverine's handler pipeline and
    /// therefore doesn't get <c>AutoApplyTransactions</c> for free. Inside Wolverine-driven
    /// handlers (<c>ProcessPaymentHandler</c>), the policy wraps the handler invocation and
    /// this method isn't needed.
    /// </para>
    /// </summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default);
}
