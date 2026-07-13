using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace NotificationService.Features;

/// <summary>
/// The "send notification" vertical slice — command, port, and handler co-located.
///
/// <para>
/// <b>Why everything in one file:</b> VSA puts the things that change together in one place.
/// The record, its handler, and the port the handler depends on all move together if this
/// feature changes. There's no project boundary to enforce — discipline does the work that
/// Clean Architecture's layer-projects used to do. Trade: less compile-time enforcement,
/// faster orientation on "show me the SendNotification flow."
/// </para>
/// <para>
/// <b>No domain aggregate:</b> stateless service, no persisted state worth protecting. See the
/// CLAUDE.md "Rich Domain Entities (when warranted)" rule.
/// </para>
/// </summary>
public record SendNotificationRequest(
    Guid RecipientId,
    string RecipientEmail,
    string Subject,
    string Body,
    string Channel = "Email");

/// <summary>
/// Port for sending a notification through a delivery channel. Dev-time
/// <c>ConsoleNotificationSender</c> logs instead of dispatching; production adapter (SendGrid,
/// Twilio, SES) swaps in via DI without touching this feature.
/// </summary>
public interface INotificationSender
{
    Task SendAsync(string recipientEmail, string subject, string body, string channel, CancellationToken ct = default);
}

/// <summary>
/// Single point of egress for notifications. Every <see cref="SendNotificationRequest"/> —
/// cascaded in-process by the event handlers (Wolverine dispatches their return value) — lands
/// here. Validates
/// the email shape, delivers via <see cref="INotificationSender"/>, increments the metric,
/// re-throws so Wolverine can retry on transient failures.
/// </summary>
public partial class SendNotificationHandler(
    INotificationSender sender,
    ILogger<SendNotificationHandler> logger)
{
    private static readonly Counter<long> NotificationsSent =
        new Meter("NextAurora").CreateCounter<long>("notifications.sent");

    public async Task HandleAsync(SendNotificationRequest request, CancellationToken cancellationToken)
    {
        // Minimal-but-real email shape check. Full RFC 5322 validation isn't necessary or useful
        // — most "valid" RFC addresses are still wrong in practice. A '@' and a length cap
        // catches the obviously-broken cases without false positives.
        if (string.IsNullOrWhiteSpace(request.RecipientEmail)
            || !request.RecipientEmail.Contains('@', StringComparison.Ordinal)
            || request.RecipientEmail.Length > 254)
        {
            throw new ArgumentException("Invalid email address format.", nameof(request));
        }

        try
        {
            await sender.SendAsync(request.RecipientEmail, request.Subject, request.Body, request.Channel, cancellationToken);

            // Channel tag on the metric lets us slice success rate by Email/SMS/Push if we
            // ever add other channels.
            NotificationsSent.Add(1, new KeyValuePair<string, object?>("channel", request.Channel));
            LogNotificationSent(logger, request.RecipientEmail, request.Subject);
        }
        catch (Exception ex)
        {
            // Log and re-throw: the retry happens at the Wolverine layer; we just record the
            // attempt's failure. Without re-throwing, retries wouldn't fire.
            LogNotificationFailed(logger, ex, request.RecipientEmail);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification sent to {Email}: {Subject}")]
    private static partial void LogNotificationSent(ILogger logger, string email, string subject);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send notification to {Email}")]
    private static partial void LogNotificationFailed(ILogger logger, Exception ex, string email);
}
