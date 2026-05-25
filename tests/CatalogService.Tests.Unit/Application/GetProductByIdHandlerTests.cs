using AwesomeAssertions;
using CatalogService.Application.Handlers;
using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using NextAurora.Contracts.DTOs;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace CatalogService.Tests.Unit.Application;

public class GetProductByIdHandlerTests
{
    private readonly IProductReadStore _readStore = Substitute.For<IProductReadStore>();
    private readonly IProductCache _cache = Substitute.For<IProductCache>();
    private readonly GetProductByIdHandler _sut;

    public GetProductByIdHandlerTests()
    {
        _sut = new GetProductByIdHandler(_readStore, _cache);

        // Mock the cache to ALWAYS invoke the factory it receives — this lets us test the
        // handler's read-store delegation independently of the cache framework. With a real
        // HybridCache we'd test cache hit/miss separately as an integration test (see
        // ProductCachingTests); here we trust the framework and verify the wiring.
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
    public async Task Handle_WhenProductExists_ReturnsDtoFromReadStore()
    {
        // ARRANGE — On cache miss the factory hits IProductReadStore.GetByIdAsync, which
        // projects in EF and returns ProductDto directly (no entity hop — see
        // docs/cqrs-data-access.md). The cache stub above forwards straight to the factory
        // so this exercises the load path end-to-end.
        var id = Guid.NewGuid();
        var dto = new ProductDto { Id = id, Name = "Widget", Price = 9.99m };
        _readStore.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(dto);

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetProductByIdQuery(id), CancellationToken.None);

        // ASSERT — DTO passes through unchanged. The read store IS the source of truth for
        // the DTO shape — there's no mapper or projection step in the handler.
        result.Should().BeSameAs(dto);
    }

    [Fact]
    public async Task Handle_WhenProductNotFound_ReturnsNull()
    {
        // ARRANGE — Read store returns null. The endpoint translates this to a 404. Null is
        // the unambiguous "not found" signal. The cache will also store the null result
        // (negative caching) — see GetProductByIdHandler doc comment.
        _readStore.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ReturnsNull();

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetProductByIdQuery(Guid.NewGuid()), CancellationToken.None);

        // ASSERT — Null DTO surfaces as 404 at the endpoint.
        result.Should().BeNull();
    }

    [Fact]
    public async Task Handle_DelegatesToCache()
    {
        // ARRANGE — The handler MUST route through IProductCache.GetOrLoadAsync rather than
        // hitting the read store directly. Without this, every read would be a DB round-trip
        // and the HybridCache investment is wasted. This is the structural check that the
        // cache-aside pattern is in place; the cache's stampede protection and L1+L2
        // behaviour are verified at the integration layer (ProductCachingTests).
        var id = Guid.NewGuid();
        _readStore.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(new ProductDto { Id = id, Name = "Widget" });

        // ACT — Run the handler against the query.
        var result = await _sut.HandleAsync(new GetProductByIdQuery(id), CancellationToken.None);

        // ASSERT — Exactly one cache call for the right product id + the cache-mediated
        // result surfaces to the caller.
        await _cache.Received(1).GetOrLoadAsync(
            id,
            Arg.Any<Func<CancellationToken, Task<ProductDto?>>>(),
            Arg.Any<CancellationToken>());
        result.Should().NotBeNull();
    }
}
