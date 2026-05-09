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
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ReturnsCachedDtoWithoutHittingRepository()
    {
        var dto = new ProductDto { Id = Guid.NewGuid(), Name = "Cached", Price = 9.99m };
        _cache.GetAsync(dto.Id, Arg.Any<CancellationToken>()).Returns(dto);

        var result = await _sut.HandleAsync(new GetProductByIdQuery(dto.Id), CancellationToken.None);

        result.Should().BeSameAs(dto);
        await _repository.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCacheMissAndProductExists_LoadsFromRepoAndPopulatesCache()
    {
        var product = ProductBuilder.Default().Build();
        _cache.GetAsync(product.Id, Arg.Any<CancellationToken>()).Returns((ProductDto?)null);
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        var result = await _sut.HandleAsync(new GetProductByIdQuery(product.Id), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(product.Id);
        result.Name.Should().Be(product.Name);
        result.Price.Should().Be(product.Price);
        await _cache.Received(1).SetAsync(Arg.Is<ProductDto>(d => d.Id == product.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCacheMissAndProductNotFound_ReturnsNullAndDoesNotPopulateCache()
    {
        _cache.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ProductDto?)null);
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Product?)null);

        var result = await _sut.HandleAsync(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        result.Should().BeNull();
        await _cache.DidNotReceive().SetAsync(Arg.Any<ProductDto>(), Arg.Any<CancellationToken>());
    }
}
