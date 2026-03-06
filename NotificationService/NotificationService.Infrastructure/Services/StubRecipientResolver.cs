using NotificationService.Application.Interfaces;

namespace NotificationService.Infrastructure.Services;

public class StubRecipientResolver : IRecipientResolver
{
    public Task<RecipientInfo?> ResolveByBuyerIdAsync(Guid buyerId, CancellationToken ct = default)
    {
        // In production, this would call an Identity/User service via gRPC
        var email = $"buyer-{buyerId:N}@placeholder.local";
        return Task.FromResult<RecipientInfo?>(new RecipientInfo(buyerId, email));
    }

    public Task<RecipientInfo?> ResolveByOrderIdAsync(Guid orderId, CancellationToken ct = default)
    {
        // In production, this would call the Order service to get the BuyerId, then resolve email
        var placeholderBuyerId = orderId;
        var email = $"order-{orderId:N}@placeholder.local";
        return Task.FromResult<RecipientInfo?>(new RecipientInfo(placeholderBuyerId, email));
    }
}
