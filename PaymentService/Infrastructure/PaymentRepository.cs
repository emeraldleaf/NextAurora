using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;
using PaymentService.Infrastructure.Data;

namespace PaymentService.Infrastructure;

/// <summary>
/// EF Core implementation of <see cref="IPaymentRepository"/>. Both <see cref="GetByIdAsync"/>
/// and <see cref="GetByOrderIdAsync"/> are shared with the command path
/// (<c>ProcessPaymentHandler</c> uses <c>GetByOrderIdAsync</c> as the idempotency check then
/// later mutates and updates the entity), so tracking stays ON for both — see
/// <c>docs/cqrs-data-access.md</c> for the rationale.
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
