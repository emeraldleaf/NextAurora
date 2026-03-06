using FluentAssertions;
using PaymentService.Domain.Entities;
using PaymentService.Tests.Unit.Builders;

namespace PaymentService.Tests.Unit.Domain;

public class PaymentTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsPendingPayment()
    {
        // Arrange & Act
        var payment = PaymentBuilder.Default().Build();

        // Assert
        payment.Status.Should().Be(PaymentStatus.Pending);
        payment.Id.Should().NotBeEmpty();
        payment.OrderId.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WithEmptyOrderId_ThrowsArgumentException()
    {
        // Arrange
        var act = () => PaymentBuilder.Default().WithOrderId(Guid.Empty).Build();

        // Act & Assert
        act.Should().Throw<ArgumentException>().WithParameterName("orderId");
    }

    [Fact]
    public void Create_WithZeroAmount_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var act = () => PaymentBuilder.Default().WithAmount(0m).Build();

        // Act & Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("amount");
    }

    [Fact]
    public void Create_WithNegativeAmount_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var act = () => PaymentBuilder.Default().WithAmount(-1m).Build();

        // Act & Assert
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("amount");
    }

    [Fact]
    public void Create_WithEmptyCurrency_ThrowsArgumentException()
    {
        // Arrange
        var act = () => PaymentBuilder.Default().WithCurrency("").Build();

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithEmptyProvider_ThrowsArgumentException()
    {
        // Arrange
        var act = () => PaymentBuilder.Default().WithProvider("").Build();

        // Act & Assert
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkAsCompleted_WhenPending_SetsStatusToCompleted()
    {
        // Arrange
        var payment = PaymentBuilder.Default().Build();

        // Act
        payment.MarkAsCompleted("txn_123");

        // Assert
        payment.Status.Should().Be(PaymentStatus.Completed);
        payment.ExternalTransactionId.Should().Be("txn_123");
        payment.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsCompleted_WhenNotPending_ThrowsInvalidOperationException()
    {
        // Arrange
        var payment = PaymentBuilder.Default().Build();
        payment.MarkAsCompleted("txn_123");

        // Act
        var act = () => payment.MarkAsCompleted("txn_456");

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAsFailed_WhenPending_SetsStatusToFailed()
    {
        // Arrange
        var payment = PaymentBuilder.Default().Build();

        // Act
        payment.MarkAsFailed("Insufficient funds");

        // Assert
        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.FailureReason.Should().Be("Insufficient funds");
    }

    [Fact]
    public void MarkAsFailed_WhenNotPending_ThrowsInvalidOperationException()
    {
        // Arrange
        var payment = PaymentBuilder.Default().Build();
        payment.MarkAsFailed("Failed");

        // Act
        var act = () => payment.MarkAsFailed("Another reason");

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }
}
