namespace NotificationService.Domain.Interfaces;

public interface INotificationSender
{
    Task SendAsync(string recipientEmail, string subject, string body, string channel, CancellationToken ct = default);
}
