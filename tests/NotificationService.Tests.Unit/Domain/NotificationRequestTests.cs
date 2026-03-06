using FluentAssertions;
using NotificationService.Domain.Entities;

namespace NotificationService.Tests.Unit.Domain;

public class NotificationRequestTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsPendingNotification()
    {
        // Arrange & Act
        var notification = NotificationRequest.Create(
            Guid.NewGuid(), "user@example.com", "Email", "Subject", "Body");

        // Assert
        notification.Status.Should().Be(NotificationStatus.Pending);
        notification.Id.Should().NotBeEmpty();
        notification.RecipientEmail.Should().Be("user@example.com");
    }

    [Fact]
    public void Create_WithEmptyEmail_ShouldThrow()
    {
        // Arrange
        var act = () => NotificationRequest.Create(Guid.NewGuid(), "", "Email", "Subject", "Body");

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithEmptySubject_ShouldThrow()
    {
        // Arrange
        var act = () => NotificationRequest.Create(Guid.NewGuid(), "user@test.com", "Email", "", "Body");

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithEmptyRecipientId_ShouldThrow()
    {
        // Arrange
        var act = () => NotificationRequest.Create(Guid.Empty, "user@test.com", "Email", "Subject", "Body");

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkAsSent_SetsTimestamp()
    {
        // Arrange
        var notification = NotificationRequest.Create(
            Guid.NewGuid(), "user@test.com", "Email", "Subject", "Body");
        var before = DateTime.UtcNow;

        // Act
        notification.MarkAsSent();

        // Assert
        notification.Status.Should().Be(NotificationStatus.Sent);
        notification.SentAt.Should().NotBeNull();
        notification.SentAt!.Value.Should().BeOnOrAfter(before);
    }
}
