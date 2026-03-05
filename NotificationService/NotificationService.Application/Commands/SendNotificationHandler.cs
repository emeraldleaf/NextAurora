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

public class SendNotificationHandler(
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
            logger.LogInformation("Notification sent to {Email}: {Subject}", request.RecipientEmail, request.Subject);
        }
        catch (Exception ex)
        {
            notification.MarkAsFailed();
            logger.LogError(ex, "Failed to send notification to {Email}", request.RecipientEmail);
        }
    }
}
