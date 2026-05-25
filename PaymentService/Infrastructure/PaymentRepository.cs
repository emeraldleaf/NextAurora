using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;
using PaymentService.Infrastructure.Data;

namespace PaymentService.Infrastructure;

/// <summary>
/// EF Core implementation of <see cref="IPaymentRepository"/>. PaymentService has no
/// query-handler read path today — both <see cref="GetByIdAsync"/> (used by
/// <c>PaymentRecoveryJob</c>) and <see cref="GetByOrderIdAsync"/> (used by
/// <c>ProcessPaymentHandler</c> as the idempotency check) load the tracked
/// <see cref="Payment"/> aggregate for mutation paths. If a read endpoint is ever added,
/// follow the read/write split rule in <c>docs/cqrs-data-access.md</c>: add a sibling
/// DTO-returning method (e.g. <c>GetSummaryByIdAsync</c>) that projects in EF; keep the
/// entity-returning loaders for the write path.
/// </summary>
public class PaymentRepository(PaymentDbContext context) : IPaymentRepository
{
    public async Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<Payment?> GetByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => await context.Payments.FirstOrDefaultAsync(p => p.OrderId == orderId, ct);

    public async Task AddAsync(Payment payment, CancellationToken ct = default)
    {
        await context.Payments.AddAsync(payment, ct);
        await context.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Payment payment, CancellationToken ct = default)
    {
        context.Payments.Update(payment);
        await context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetStalePendingPaymentIdsAsync(DateTime olderThan, CancellationToken ct = default)
        => await context.Payments
            .AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Pending && p.CreatedAt < olderThan)
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.Id)
            .ToListAsync(ct);

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        await using var tx = await context.Database.BeginTransactionAsync(ct);
        await work(ct);

        // CRITICAL: SaveChangesAsync after work() is what flushes Wolverine's staged
        // outbox envelopes. Wolverine's UseEntityFrameworkCoreTransactions bridge
        // intercepts SaveChanges to persist the wolverine.outgoing_envelopes rows
        // *in the same transaction* as the entity write. Without this call,
        // PublishAsync(...) inside work() stages envelopes in the change tracker
        // but they never reach the DB — entity commits, event is silently dropped.
        // The intermediate UpdateAsync inside work() flushes the entity, but the
        // outbox row is staged *after* that point and needs a second flush here.
        // See CLAUDE.md "Performance Rules — Outbox atomicity" + Wolverine outbox docs.
        await context.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
    }
}
