using AwesomeAssertions;
using CatalogService.Features;

namespace CatalogService.Tests.Unit.Application;

public class ReserveStockCommandValidatorTests
{
    private readonly ReserveStockCommandValidator _sut = new();

    private static ReserveStockCommand ValidCommand() => new(Guid.NewGuid(), Quantity: 5);

    [Fact]
    public void Validate_WithValidCommand_ReturnsNoErrors()
    {
        var command = ValidCommand();

        var result = _sut.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmptyProductId_ReturnsError()
    {
        var command = ValidCommand() with { ProductId = Guid.Empty };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ProductId");
    }

    [Fact]
    public void Validate_WithZeroQuantity_ReturnsError()
    {
        var command = ValidCommand() with { Quantity = 0 };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Quantity");
    }

    [Fact]
    public void Validate_WithNegativeQuantity_ReturnsError()
    {
        var command = ValidCommand() with { Quantity = -1 };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Quantity");
    }

    [Fact]
    public void Validate_WithQuantityAtUpperBound_ReturnsNoErrors()
    {
        // The validator caps at 10_000 — boundary value should pass.
        var command = ValidCommand() with { Quantity = 10_000 };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithQuantityOverUpperBound_ReturnsError()
    {
        var command = ValidCommand() with { Quantity = 10_001 };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Quantity");
    }
}
