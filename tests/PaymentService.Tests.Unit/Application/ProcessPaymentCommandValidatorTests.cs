using FluentAssertions;
using PaymentService.Application.Commands;
using PaymentService.Application.Validators;

namespace PaymentService.Tests.Unit.Application;

public class ProcessPaymentCommandValidatorTests
{
    private readonly ProcessPaymentCommandValidator _sut = new();

    [Fact]
    public void Validate_WithValidCommand_ReturnsNoErrors()
    {
        var result = _sut.Validate(new ProcessPaymentCommand(Guid.NewGuid(), 50m, "USD"));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmptyOrderId_ReturnsError()
    {
        var result = _sut.Validate(new ProcessPaymentCommand(Guid.Empty, 50m, "USD"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithZeroAmount_ReturnsError()
    {
        var result = _sut.Validate(new ProcessPaymentCommand(Guid.NewGuid(), 0m, "USD"));
        result.IsValid.Should().BeFalse();
    }
}
