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
        var result = _sut.Validate(ValidCommand());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithEmptyName_ReturnsError()
    {
        var result = _sut.Validate(ValidCommand() with { Name = "" });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Name");
    }
}
