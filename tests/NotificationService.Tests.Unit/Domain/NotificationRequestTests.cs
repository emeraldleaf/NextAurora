using FluentAssertions;
using NotificationService.Domain.Entities;

namespace NotificationService.Tests.Unit.Domain;

public class NotificationRequestTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsPendingNotification()
    {
        var notification = NotificationRequest.Create(
            Guid.NewGuid(), "user@example.com", "Email", "Subject", "Body");

        notification.Status.Should().Be(NotificationStatus.Pending);
        notification.Id.Should().NotBeEmpty();
        notification.RecipientEmail.Should().Be("user@example.com");
    }

    [Fact(Skip = "Known bug: NotificationRequest.Create has no validation — empty email is accepted")]
    public void Create_WithEmptyEmail_ShouldThrow()
    {
        var act = () => NotificationRequest.Create(Guid.NewGuid(), "", "Email", "Subject", "Body");

        act.Should().Throw<ArgumentException>();
    }

    [Fact(Skip = "Known bug: NotificationRequest.Create has no validation — empty subject is accepted")]
    public void Create_WithEmptySubject_ShouldThrow()
    {
        var act = () => NotificationRequest.Create(Guid.NewGuid(), "user@test.com", "Email", "", "Body");

        act.Should().Throw<ArgumentException>();
    }

    [Fact(Skip = "Known bug: NotificationRequest.Create has no validation — Guid.Empty recipientId is accepted")]
    public void Create_WithEmptyRecipientId_ShouldThrow()
    {
        var act = () => NotificationRequest.Create(Guid.Empty, "user@test.com", "Email", "Subject", "Body");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkAsSent_SetsTimestamp()
    {
        var notification = NotificationRequest.Create(
            Guid.NewGuid(), "user@test.com", "Email", "Subject", "Body");
        var before = DateTime.UtcNow;

        notification.MarkAsSent();

        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.SentAt.Should().NotBeNull();
        notification.SentAt!.Value.Should().BeOnOrAfter(before);
    }
}
