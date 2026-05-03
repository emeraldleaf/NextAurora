namespace NotificationService.Domain.Entities;

/// <summary>
/// In-memory aggregate for a single notification attempt — the only domain entity in
/// NotificationService. Same factory + private setters + status-guard pattern as the other
/// services' aggregates.
///
/// <para>
/// <b>Why "in-memory":</b> NotificationService is currently stateless — there's no database,
/// no DbContext. The <see cref="NotificationRequest"/> is created from an incoming event, sent
/// via <see cref="Domain.Interfaces.INotificationSender"/>, and discarded. If we ever need a
/// persistent record (delivery audit, retry-from-store), giving this service its own DB and
/// wiring up <c>SaveChanges</c> is a small change — the entity is already shaped for it.
/// </para>
/// </summary>
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

    /// <summary>
    /// Factory — the only way to create one. Validates email shape (contains '@', length cap)
    /// at the domain layer rather than relying on a separate validator: notifications are
    /// always created from event handlers, not from validated commands, so the domain is the
    /// last line of defense against bad data going to a real email provider.
    /// </summary>
    public static NotificationRequest Create(Guid recipientId, string recipientEmail, string channel, string subject, string body)
    {
        if (recipientId == Guid.Empty)
            throw new ArgumentException("Recipient ID must not be empty.", nameof(recipientId));

        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail);
        // Minimal-but-real email shape check. Full RFC 5322 validation isn't necessary or
        // useful — most "valid" RFC addresses are still wrong in practice. A '@' and a length
        // cap catches the obviously-broken cases without false positives.
        if (!recipientEmail.Contains('@', StringComparison.Ordinal) || recipientEmail.Length > 254)
            throw new ArgumentException("Invalid email address format.", nameof(recipientEmail));
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
