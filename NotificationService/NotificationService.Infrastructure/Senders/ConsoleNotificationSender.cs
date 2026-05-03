using Microsoft.Extensions.Logging;
using NotificationService.Domain.Interfaces;

namespace NotificationService.Infrastructure.Senders;

/// <summary>
/// Development implementation of <see cref="INotificationSender"/> — just logs the notification
/// instead of dispatching it anywhere. Visible in the Aspire dashboard as structured log lines,
/// making it easy to verify the saga is producing the right notifications during local runs.
///
/// <para>
/// <b>Open/Closed in practice:</b> in production, register a <c>SendGridNotificationSender</c>
/// or <c>TwilioNotificationSender</c> instead of this — handlers don't change. The
/// <see cref="INotificationSender"/> abstraction is the seam.
/// </para>
/// </summary>
public partial class ConsoleNotificationSender(ILogger<ConsoleNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(string recipientEmail, string subject, string body, string channel, CancellationToken ct = default)
    {
        LogNotification(logger, channel, recipientEmail, subject, body);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[{Channel}] To: {Email} | Subject: {Subject} | Body: {Body}")]
    private static partial void LogNotification(ILogger logger, string channel, string email, string subject, string body);
}
