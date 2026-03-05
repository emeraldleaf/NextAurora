using FluentAssertions;
using OrderService.Application.Commands;
using OrderService.Application.Validators;

namespace OrderService.Tests.Unit.Application;

public class PlaceOrderCommandValidatorTests
{
    private readonly PlaceOrderCommandValidator _sut = new();

    private static PlaceOrderCommand ValidCommand() =>
        new(Guid.NewGuid(), "USD", [new PlaceOrderLineItem(Guid.NewGuid(), "Product", 1, 10m)]);

    [Fact]
    public void Validate_WithValidCommand_ReturnsNoErrors()
    {
        var result = _sut.Validate(ValidCommand());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmptyBuyerId_ReturnsError()
    {
        var command = ValidCommand() with { BuyerId = Guid.Empty };
        var result = _sut.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "BuyerId");
    }

    [Fact]
    public void Validate_WithEmptyLines_ReturnsError()
    {
        var command = ValidCommand() with { Lines = [] };
        var result = _sut.Validate(command);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithInvalidCurrencyLength_ReturnsError()
    {
        var command = ValidCommand() with { Currency = "US" };
        var result = _sut.Validate(command);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Currency");
    }

    [Fact]
    public void Validate_WithLineHavingEmptyProductId_ReturnsError()
    {
        var command = ValidCommand() with { Lines = [new PlaceOrderLineItem(Guid.Empty, "P", 1, 10m)] };
        var result = _sut.Validate(command);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_WithLineHavingZeroQuantity_ReturnsError()
    {
        var command = ValidCommand() with { Lines = [new PlaceOrderLineItem(Guid.NewGuid(), "P", 0, 10m)] };
        var result = _sut.Validate(command);
        result.IsValid.Should().BeFalse();
    }
}
