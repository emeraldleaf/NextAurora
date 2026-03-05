using Microsoft.Extensions.Logging;
using NotificationService.Domain.Interfaces;

namespace NotificationService.Infrastructure.Senders;

public partial class ConsoleNotificationSender(ILogger<ConsoleNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(string recipientEmail, string subject, string body, string channel, CancellationToken ct = default)
    {
        // In production, this would integrate with SendGrid, Twilio, etc.
        LogNotification(logger, channel, recipientEmail, subject, body);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Channel}] To: {Email} | Subject: {Subject} | Body: {Body}")]
    private static partial void LogNotification(ILogger logger, string channel, string email, string subject, string body);
}
