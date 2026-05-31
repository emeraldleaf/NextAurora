using AwesomeAssertions;
using CatalogService.Features;

namespace CatalogService.Tests.Unit.Application;

public class UpdateProductCommandValidatorTests
{
    private readonly UpdateProductCommandValidator _sut = new();

    private static UpdateProductCommand ValidCommand() =>
        new(Guid.NewGuid(), "seller-1", "Widget v2", "Updated widget", 24.99m);

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
    public void Validate_WithEmptySellerId_ReturnsError()
    {
        var command = ValidCommand() with { SellerId = "" };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "SellerId");
    }

    [Fact]
    public void Validate_WithEmptyName_ReturnsError()
    {
        var command = ValidCommand() with { Name = "" };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }

    [Fact]
    public void Validate_WithNameOver200Chars_ReturnsError()
    {
        var command = ValidCommand() with { Name = new string('x', 201) };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }

    [Fact]
    public void Validate_WithDescriptionOver2000Chars_ReturnsError()
    {
        var command = ValidCommand() with { Description = new string('x', 2001) };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Description");
    }

    [Fact]
    public void Validate_WithZeroPrice_ReturnsError()
    {
        var command = ValidCommand() with { Price = 0m };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Price");
    }

    [Fact]
    public void Validate_WithNegativePrice_ReturnsError()
    {
        var command = ValidCommand() with { Price = -1m };

        var result = _sut.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Price");
    }
}
