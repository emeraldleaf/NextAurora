using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using NotificationService.Domain.Entities;
using NotificationService.Domain.Interfaces;

namespace NotificationService.Application.Commands;

/// <summary>
/// The command — produced by every notification event handler in this service. Travels through
/// Wolverine's pipeline like any other command. Could also be sent directly to the
/// <c>send-notification</c> Service Bus queue from any other service that wants to trigger an
/// ad-hoc notification.
/// </summary>
public record SendNotificationRequest(
    Guid RecipientId,
    string RecipientEmail,
    string Subject,
    string Body,
    string Channel = "Email");

/// <summary>
/// Single point of egress for notifications: every <see cref="SendNotificationRequest"/>
/// (whether produced by an event handler or sent directly to the queue) lands here. One handler
/// owns the actual delivery, increments the metric, and decides what counts as a permanent
/// vs transient failure.
///
/// <para>
/// <b>SOLID — Single Responsibility:</b> this handler doesn't know what triggered the request
/// (order placed? payment failed? admin push?) — that's the calling event handler's concern.
/// It just delivers. <c>INotificationSender</c> is in turn the abstraction that hides whether
/// we're using SMTP, SendGrid, or the dev-time console sink.
/// </para>
/// <para>
/// <b>Why we re-throw on failure:</b> Wolverine's retry policy on this handler chain treats a
/// thrown exception as a transient failure and will redeliver. The notification is still
/// marked as Failed in the local <see cref="NotificationRequest"/> aggregate, but since this
/// service is currently stateless, that audit trail is in-memory only. Once we add a DB,
/// failed notifications get an audit row that survives the retry.
/// </para>
/// <para>
/// <b>Source-generated logging</b> for both the success and failure paths — see the
/// <see cref="StripePaymentGateway"/> notes for why this matters on hot paths.
/// </para>
/// </summary>
public partial class SendNotificationHandler(
    INotificationSender sender,
    ILogger<SendNotificationHandler> logger)
{
    private static readonly Counter<long> NotificationsSent =
        new Meter("NextAurora").CreateCounter<long>("notifications.sent");

    public async Task HandleAsync(SendNotificationRequest request, CancellationToken cancellationToken)
    {
        // Domain validation up-front — bad inputs throw before we hit any delivery infrastructure.
        var notification = NotificationRequest.Create(
            request.RecipientId, request.RecipientEmail, request.Channel,
            request.Subject, request.Body);

        try
        {
            await sender.SendAsync(request.RecipientEmail, request.Subject, request.Body, request.Channel, cancellationToken);
            notification.MarkAsSent();

            // Channel tag on the metric lets us slice success rate by Email/SMS/Push if we
            // ever add other channels.
            NotificationsSent.Add(1, new KeyValuePair<string, object?>("channel", request.Channel));
            LogNotificationSent(logger, request.RecipientEmail, request.Subject);
        }
        catch (Exception ex)
        {
            // Mark and re-throw: the retry happens at the Wolverine layer; we just record the
            // attempt's failure. Without re-throwing, retries wouldn't fire.
            notification.MarkAsFailed();
            LogNotificationFailed(logger, ex, request.RecipientEmail);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification sent to {Email}: {Subject}")]
    private static partial void LogNotificationSent(ILogger logger, string email, string subject);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send notification to {Email}")]
    private static partial void LogNotificationFailed(ILogger logger, Exception ex, string email);
}
