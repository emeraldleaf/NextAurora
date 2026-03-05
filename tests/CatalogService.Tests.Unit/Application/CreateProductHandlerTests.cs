using CatalogService.Application.Commands;
using CatalogService.Application.Handlers;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using FluentAssertions;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class CreateProductHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly CreateProductHandler _sut;

    public CreateProductHandlerTests()
    {
        _sut = new CreateProductHandler(_repository);
    }

    [Fact]
    public async Task Handle_WithValidCommand_CreatesProductAndReturnsId()
    {
        var command = new CreateProductCommand("Widget", "A widget", 19.99m, "USD", Guid.NewGuid(), "seller-1", 10);

        var result = await _sut.Handle(command, CancellationToken.None);

        result.Should().NotBeEmpty();
        await _repository.Received(1).AddAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }
}
