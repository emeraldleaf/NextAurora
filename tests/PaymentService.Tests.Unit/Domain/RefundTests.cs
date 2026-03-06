using FluentAssertions;
using PaymentService.Domain.Entities;

namespace PaymentService.Tests.Unit.Domain;

public class RefundTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsPendingRefund()
    {
        // Arrange & Act
        var refund = Refund.Create(Guid.NewGuid(), 50m, "Customer request");

        // Assert
        refund.Status.Should().Be(RefundStatus.Pending);
        refund.Id.Should().NotBeEmpty();
        refund.Amount.Should().Be(50m);
        refund.Reason.Should().Be("Customer request");
    }

    [Fact]
    public void Create_WithNegativeAmount_ShouldThrow()
    {
        // Arrange
        var act = () => Refund.Create(Guid.NewGuid(), -50m, "Reason");

        // Act & Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithZeroAmount_ShouldThrow()
    {
        // Arrange
        var act = () => Refund.Create(Guid.NewGuid(), 0m, "Reason");

        // Act & Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_WithEmptyPaymentId_ShouldThrow()
    {
        // Arrange
        var act = () => Refund.Create(Guid.Empty, 50m, "Reason");

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithEmptyReason_ShouldThrow()
    {
        // Arrange
        var act = () => Refund.Create(Guid.NewGuid(), 50m, "");

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkAsProcessed_WhenAlreadyProcessed_ShouldThrow()
    {
        // Arrange
        var refund = Refund.Create(Guid.NewGuid(), 50m, "Reason");
        refund.MarkAsProcessed();

        // Act
        var act = () => refund.MarkAsProcessed();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
