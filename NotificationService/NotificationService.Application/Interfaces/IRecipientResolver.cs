namespace NotificationService.Application.Interfaces;

public interface IRecipientResolver
{
    Task<RecipientInfo?> ResolveByBuyerIdAsync(Guid buyerId, CancellationToken ct = default);
    Task<RecipientInfo?> ResolveByOrderIdAsync(Guid orderId, CancellationToken ct = default);
}

public record RecipientInfo(Guid BuyerId, string Email);
