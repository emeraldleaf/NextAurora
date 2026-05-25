using AwesomeAssertions;
using CatalogService.Application.Handlers;
using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Tests.Unit.Builders;
using NextAurora.Contracts.DTOs;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace CatalogService.Tests.Unit.Application;

public class GetProductByIdHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly IProductCache _cache = Substitute.For<IProductCache>();
    private readonly GetProductByIdHandler _sut;

    public GetProductByIdHandlerTests()
    {
        _sut = new GetProductByIdHandler(_repository, _cache);

        // Mock the cache to ALWAYS invoke the factory it receives — this lets us test the
        // handler's projection logic (Product → ProductDto mapping) independently of the
        // cache framework. With a real HybridCache we'd test cache hit/miss separately as
        // an integration test (see ProductCachingTests); here we trust the framework and
        // verify the delegation.
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
        // ARRANGE — A real Product (so ProductMapper has real fields to project from)
        // returned by the repository. The cache stub above forwards straight to the
        // factory, so this exercises the load path end-to-end.
        var product = ProductBuilder.Default().Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetProductByIdQuery(product.Id), CancellationToken.None);

        // ASSERT — Three invariants:
        //  1) Result is non-null (the product was found).
        //  2) Id matches what we asked for (defensive — guarantees we got the right one).
        //  3) Name and Price round-trip from the entity to the DTO. The full mapping
        //     contract lives in ProductMapper; here we verify the most important fields
        //     so a future refactor that drops a field surfaces immediately.
        result.Should().NotBeNull();
        result!.Id.Should().Be(product.Id);
        result.Name.Should().Be(product.Name);
        result.Price.Should().Be(product.Price);
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ReturnsNull()
    {
        // ARRANGE — Repository returns null. The endpoint translates this to a 404. Null
        // is the unambiguous "not found" signal — a sentinel like Guid.Empty would force
        // every caller into special-case handling.
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Null DTO surfaces as 404 at the endpoint.
        result.Should().BeNull();
    }

    [Fact]
    public async Task Handle_DelegatesToCache()
    {
        // ARRANGE — The handler MUST route through IProductCache.GetOrLoadAsync rather
        // than hitting the repository directly. Without this, every read would be a DB
        // round-trip and the HybridCache investment is wasted. This is the structural
        // check that the cache-aside pattern is in place; the cache's stampede protection
        // and L1+L2 behaviour are verified at the integration layer (ProductCachingTests).
        var product = ProductBuilder.Default().Build();
        _repository.GetByIdAsync(product.Id, Arg.Any<CancellationToken>()).Returns(product);

        // ACT — Run the handler against the query.
        await _sut.HandleAsync(new GetProductByIdQuery(product.Id), CancellationToken.None);

        // ASSERT — Exactly one cache call, for the right product id.
        await _cache.Received(1).GetOrLoadAsync(
            product.Id,
            Arg.Any<Func<CancellationToken, Task<ProductDto?>>>(),
            Arg.Any<CancellationToken>());
    }
}
