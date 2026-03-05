namespace NotificationService.Domain.Entities;

public class NotificationRequest
{
    public Guid Id { get; private set; }
    public Guid RecipientId { get; private set; }
    public string RecipientEmail { get; private set; } = "";
    public string Channel { get; private set; } = "Email";
    public string Subject { get; private set; } = "";
    public string Body { get; private set; } = "";
    public NotificationStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? SentAt { get; private set; }

    private NotificationRequest() { }

    public static NotificationRequest Create(Guid recipientId, string recipientEmail, string channel, string subject, string body)
    {
        return new NotificationRequest
        {
            Id = Guid.NewGuid(),
            RecipientId = recipientId,
            RecipientEmail = recipientEmail,
            Channel = channel,
            Subject = subject,
            Body = body,
            Status = NotificationStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
    }

    public void MarkAsSent()
    {
        Status = NotificationStatus.Sent;
        SentAt = DateTime.UtcNow;
    }

    public void MarkAsFailed() => Status = NotificationStatus.Failed;
}
