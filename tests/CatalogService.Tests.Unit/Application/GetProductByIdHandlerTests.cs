using CatalogService.Application.Handlers;
using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Tests.Unit.Builders;
using FluentAssertions;
using NextAurora.Contracts.DTOs;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class GetProductByIdHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IProductCache _cache = Substitute.For<IProductCache>();
    private readonly GetProductByIdHandler _sut;

    public GetProductByIdHandlerTests()
    {
        _sut = new GetProductByIdHandler(_repository, _cache);

        // Mock the cache to always invoke the factory it receives — this lets us test the
        // handler's projection logic (Product → ProductDto mapping) independently of the
        // cache framework. With a real HybridCache we'd test cache hit/miss separately as
        // an integration test; here we trust the framework and verify the delegation.
        _cache.GetOrLoadAsync(
                Arg.Any<Guid>(),
                Arg.Any<Func<CancellationToken, Task<ProductDto?>>>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var factory = callInfo.Arg<Func<CancellationToken, Task<ProductDto?>>>();
                return factory(CancellationToken.None);
            });
    }

    [Fact]
    public async Task Handle_WhenProductExists_ReturnsMappedDto()
    {
        var product = ProductBuilder.Default().Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        var result = await _sut.HandleAsync(new GetProductByIdQuery(product.Id), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(product.Id);
        result.Name.Should().Be(product.Name);
        result.Price.Should().Be(product.Price);
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ReturnsNull()
    {
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _sut.HandleAsync(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Handle_DelegatesToCache()
    {
        var product = ProductBuilder.Default().Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        await _sut.HandleAsync(new GetProductByIdQuery(product.Id), CancellationToken.None);

        await _cache.Received(1).GetOrLoadAsync(
            product.Id,
            Arg.Any<Func<CancellationToken, Task<ProductDto?>>>(),
            Arg.Any<CancellationToken>());
    }
}
