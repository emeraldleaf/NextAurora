using Microsoft.EntityFrameworkCore;
using PaymentService.Domain.Entities;
using PaymentService.Domain.Interfaces;
using PaymentService.Infrastructure.Data;

namespace PaymentService.Infrastructure.Repositories;

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

    /// <summary>
    /// Existence check used by the saga: when <c>OrderPlacedEvent</c> redelivers, the handler
    /// calls this first and short-circuits if a Payment for this order already exists. Tracking
    /// is on because the same handler path may load and update the entity later if no payment
    /// existed yet.
    /// </summary>
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
}
