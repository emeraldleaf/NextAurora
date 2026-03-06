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
        if (recipientId == Guid.Empty)
            throw new ArgumentException("Recipient ID must not be empty.", nameof(recipientId));

        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

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
        if (Status != NotificationStatus.Pending)
            throw new InvalidOperationException("Cannot mark notification as sent in the current status.");
        Status = NotificationStatus.Sent;
        SentAt = DateTime.UtcNow;
    }

    public void MarkAsFailed()
    {
        if (Status != NotificationStatus.Pending)
            throw new InvalidOperationException("Cannot mark notification as failed in the current status.");
        Status = NotificationStatus.Failed;
    }
}
