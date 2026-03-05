using MediatR;
using Microsoft.Extensions.Logging;
using NotificationService.Domain.Entities;
using NotificationService.Domain.Interfaces;

namespace NotificationService.Application.Commands;

public record SendNotificationRequest(
    Guid RecipientId,
    string RecipientEmail,
    string Subject,
    string Body,
    string Channel = "Email") : IRequest;

public partial class SendNotificationHandler(
    INotificationSender sender,
    ILogger<SendNotificationHandler> logger) : IRequestHandler<SendNotificationRequest>
{
    public async Task Handle(SendNotificationRequest request, CancellationToken cancellationToken)
    {
        var notification = NotificationRequest.Create(
            request.RecipientId, request.RecipientEmail, request.Channel,
            request.Subject, request.Body);

        try
        {
            await sender.SendAsync(request.RecipientEmail, request.Subject, request.Body, request.Channel, cancellationToken);
            notification.MarkAsSent();
            LogNotificationSent(logger, request.RecipientEmail, request.Subject);
        }
        catch (Exception ex)
        {
            notification.MarkAsFailed();
            LogNotificationFailed(logger, ex, request.RecipientEmail);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification sent to {Email}: {Subject}")]
    private static partial void LogNotificationSent(ILogger logger, string email, string subject);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send notification to {Email}")]
    private static partial void LogNotificationFailed(ILogger logger, Exception ex, string email);
}
