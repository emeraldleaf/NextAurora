using CatalogService.Application.Handlers;
using CatalogService.Application.Queries;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Tests.Unit.Builders;
using FluentAssertions;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class GetProductByIdHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly GetProductByIdHandler _sut;

    public GetProductByIdHandlerTests()
    {
        _sut = new GetProductByIdHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenProductExists_ReturnsMappedDto()
    {
        // Arrange
        var product = ProductBuilder.Default().Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        // Act
        var result = await _sut.Handle(new GetProductByIdQuery(product.Id), CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(product.Id);
        result.Name.Should().Be(product.Name);
        result.Price.Should().Be(product.Price);
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ReturnsNull()
    {
        // Arrange
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        // Act
        var result = await _sut.Handle(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }
}
