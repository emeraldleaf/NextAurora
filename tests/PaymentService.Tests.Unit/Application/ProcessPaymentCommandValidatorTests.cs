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
        // Arrange
        var command = new ProcessPaymentCommand(Guid.NewGuid(), 50m, "USD");

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmptyOrderId_ReturnsError()
    {
        // Arrange
        var command = new ProcessPaymentCommand(Guid.Empty, 50m, "USD");

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithZeroAmount_ReturnsError()
    {
        // Arrange
        var command = new ProcessPaymentCommand(Guid.NewGuid(), 0m, "USD");

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
    }
}
