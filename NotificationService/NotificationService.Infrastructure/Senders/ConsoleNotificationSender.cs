using Microsoft.Extensions.Logging;
using NotificationService.Domain.Interfaces;

namespace NotificationService.Infrastructure.Senders;

public class ConsoleNotificationSender(ILogger<ConsoleNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(string recipientEmail, string subject, string body, string channel, CancellationToken ct = default)
    {
        // In production, this would integrate with SendGrid, Twilio, etc.
        logger.LogInformation("[{Channel}] To: {Email} | Subject: {Subject} | Body: {Body}",
            channel, recipientEmail, subject, body);
        return Task.CompletedTask;
    }
}
