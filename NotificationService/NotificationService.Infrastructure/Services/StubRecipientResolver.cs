using NotificationService.Application.Interfaces;

namespace NotificationService.Infrastructure.Services;

/// <summary>
/// Stub implementation of <see cref="IRecipientResolver"/> that produces a deterministic
/// placeholder email from a buyer ID or order ID. Lets the saga end-to-end run locally without
/// a real user/identity service.
///
/// <para>
/// In production this would either:
/// </para>
/// <list type="bullet">
///   <item>Call a User/Identity service over gRPC to map buyer ID → real email, or</item>
///   <item>Read from a local cache of buyer profiles populated from a UserCreated event.</item>
/// </list>
/// <para>
/// Replacing this stub doesn't touch any handler code — they all depend on
/// <see cref="IRecipientResolver"/>.
/// </para>
/// </summary>
public class StubRecipientResolver : IRecipientResolver
{
    public Task<RecipientInfo?> ResolveByBuyerIdAsync(Guid buyerId, CancellationToken ct = default)
    {
        var email = $"buyer-{buyerId:N}@placeholder.local";
        return Task.FromResult<RecipientInfo?>(new RecipientInfo(buyerId, email));
    }

    public Task<RecipientInfo?> ResolveByOrderIdAsync(Guid orderId, CancellationToken ct = default)
    {
        // Real implementation would call OrderService over gRPC to get the BuyerId for this
        // order, then resolve buyer → email. We just shape something that won't blow up.
        var placeholderBuyerId = orderId;
        var email = $"order-{orderId:N}@placeholder.local";
        return Task.FromResult<RecipientInfo?>(new RecipientInfo(placeholderBuyerId, email));
    }
}
