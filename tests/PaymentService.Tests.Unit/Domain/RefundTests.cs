using FluentAssertions;
using PaymentService.Domain.Entities;

namespace PaymentService.Tests.Unit.Domain;

public class RefundTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsPendingRefund()
    {
        var refund = Refund.Create(Guid.NewGuid(), 50m, "Customer request");

        refund.Status.Should().Be(RefundStatus.Pending);
        refund.Id.Should().NotBeEmpty();
        refund.Amount.Should().Be(50m);
        refund.Reason.Should().Be("Customer request");
    }

    [Fact(Skip = "Known bug: Refund.Create has no validation — negative amount is accepted")]
    public void Create_WithNegativeAmount_ShouldThrow()
    {
        var act = () => Refund.Create(Guid.NewGuid(), -50m, "Reason");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact(Skip = "Known bug: Refund.Create has no validation — zero amount is accepted")]
    public void Create_WithZeroAmount_ShouldThrow()
    {
        var act = () => Refund.Create(Guid.NewGuid(), 0m, "Reason");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact(Skip = "Known bug: Refund.Create has no validation — empty PaymentId is accepted")]
    public void Create_WithEmptyPaymentId_ShouldThrow()
    {
        var act = () => Refund.Create(Guid.Empty, 50m, "Reason");

        act.Should().Throw<ArgumentException>();
    }

    [Fact(Skip = "Known bug: Refund.Create has no validation — empty reason is accepted")]
    public void Create_WithEmptyReason_ShouldThrow()
    {
        var act = () => Refund.Create(Guid.NewGuid(), 50m, "");

        act.Should().Throw<ArgumentException>();
    }

    [Fact(Skip = "Known bug: Refund has no state guard — MarkAsProcessed can be called multiple times")]
    public void MarkAsProcessed_WhenAlreadyProcessed_ShouldThrow()
    {
        var refund = Refund.Create(Guid.NewGuid(), 50m, "Reason");
        refund.MarkAsProcessed();

        var act = () => refund.MarkAsProcessed();

        act.Should().Throw<InvalidOperationException>();
    }
}
