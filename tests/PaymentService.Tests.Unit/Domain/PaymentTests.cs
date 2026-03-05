using FluentAssertions;
using PaymentService.Domain.Entities;
using PaymentService.Tests.Unit.Builders;

namespace PaymentService.Tests.Unit.Domain;

public class PaymentTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsPendingPayment()
    {
        var payment = PaymentBuilder.Default().Build();

        payment.Status.Should().Be(PaymentStatus.Pending);
        payment.Id.Should().NotBeEmpty();
        payment.OrderId.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WithEmptyOrderId_ThrowsArgumentException()
    {
        var act = () => PaymentBuilder.Default().WithOrderId(Guid.Empty).Build();

        act.Should().Throw<ArgumentException>().WithParameterName("orderId");
    }

    [Fact]
    public void Create_WithZeroAmount_ThrowsArgumentOutOfRangeException()
    {
        var act = () => PaymentBuilder.Default().WithAmount(0m).Build();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("amount");
    }

    [Fact]
    public void Create_WithNegativeAmount_ThrowsArgumentOutOfRangeException()
    {
        var act = () => PaymentBuilder.Default().WithAmount(-1m).Build();

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("amount");
    }

    [Fact]
    public void Create_WithEmptyCurrency_ThrowsArgumentException()
    {
        var act = () => PaymentBuilder.Default().WithCurrency("").Build();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_WithEmptyProvider_ThrowsArgumentException()
    {
        var act = () => PaymentBuilder.Default().WithProvider("").Build();

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkAsCompleted_WhenPending_SetsStatusToCompleted()
    {
        var payment = PaymentBuilder.Default().Build();

        payment.MarkAsCompleted("txn_123");

        payment.Status.Should().Be(PaymentStatus.Completed);
        payment.ExternalTransactionId.Should().Be("txn_123");
        payment.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void MarkAsCompleted_WhenNotPending_ThrowsInvalidOperationException()
    {
        var payment = PaymentBuilder.Default().Build();
        payment.MarkAsCompleted("txn_123");

        var act = () => payment.MarkAsCompleted("txn_456");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkAsFailed_WhenPending_SetsStatusToFailed()
    {
        var payment = PaymentBuilder.Default().Build();

        payment.MarkAsFailed("Insufficient funds");

        payment.Status.Should().Be(PaymentStatus.Failed);
        payment.FailureReason.Should().Be("Insufficient funds");
    }

    [Fact]
    public void MarkAsFailed_WhenNotPending_ThrowsInvalidOperationException()
    {
        var payment = PaymentBuilder.Default().Build();
        payment.MarkAsFailed("Failed");

        var act = () => payment.MarkAsFailed("Another reason");

        act.Should().Throw<InvalidOperationException>();
    }
}
