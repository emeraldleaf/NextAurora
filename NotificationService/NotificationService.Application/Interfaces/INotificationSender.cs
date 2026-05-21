namespace NotificationService.Application.Interfaces;

/// <summary>
/// Port for sending a notification through a delivery channel. The only abstraction this service
/// retains: the dev-time <c>ConsoleNotificationSender</c> logs instead of dispatching, and a
/// production adapter (SendGrid, Twilio, SES) swaps in via DI without touching handlers. This is
/// the one "future-swap" story in NotificationService that's concrete enough to justify a seam.
/// </summary>
public interface INotificationSender
{
    Task SendAsync(string recipientEmail, string subject, string body, string channel, CancellationToken ct = default);
}
