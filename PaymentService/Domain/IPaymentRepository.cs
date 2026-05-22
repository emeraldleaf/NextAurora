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
}
