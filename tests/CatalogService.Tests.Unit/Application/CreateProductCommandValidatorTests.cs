using CatalogService.Application.Commands;
using CatalogService.Application.Validators;
using FluentAssertions;

namespace CatalogService.Tests.Unit.Application;

public class CreateProductCommandValidatorTests
{
    private readonly CreateProductCommandValidator _sut = new();

    private static CreateProductCommand ValidCommand() =>
        new("Widget", "A widget", 19.99m, "USD", Guid.NewGuid(), "seller-1", 10);

    [Fact]
    public void Validate_WithValidCommand_ReturnsNoErrors()
    {
        // Arrange
        var command = ValidCommand();

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmptyName_ReturnsError()
    {
        // Arrange
        var command = ValidCommand() with { Name = "" };

        // Act
        var result = _sut.Validate(command);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }
}
