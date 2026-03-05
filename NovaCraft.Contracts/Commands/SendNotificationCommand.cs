namespace NovaCraft.Contracts.Commands;

public record SendNotificationCommand
{
    public Guid RecipientId { get; init; }
    public string RecipientEmail { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string Channel { get; init; } = "Email";
    public Dictionary<string, string> Metadata { get; init; } = [];
}
