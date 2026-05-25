using AwesomeAssertions;
using CatalogService.Application.Handlers;
using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using NextAurora.Contracts.DTOs;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class SearchProductsHandlerTests
{
    private readonly IProductReadStore _readStore = Substitute.For<IProductReadStore>();
    private readonly SearchProductsHandler _sut;

    public SearchProductsHandlerTests()
    {
        _sut = new SearchProductsHandler(_readStore);
    }

    [Fact]
    public async Task Handle_WhenReadStoreReturnsProducts_PassesThroughUnchanged()
    {
        // ARRANGE — The handler is a one-line passthrough to IProductReadStore.SearchAsync,
        // which projects in EF and returns DTOs directly (no entity hop, no in-memory mapper —
        // see docs/cqrs-data-access.md). We seed two DTOs and check both round-trip with the
        // pricing intact (proves the projection isn't being mangled in the handler).
        var dtos = new List<ProductDto>
        {
            new() { Id = Guid.NewGuid(), Name = "Apple", Price = 1m },
            new() { Id = Guid.NewGuid(), Name = "Banana", Price = 2m }
        };
        _readStore.SearchAsync("fruit", 1, 50, Arg.Any<CancellationToken>()).Returns(dtos);

        // ACT — Run the search query through the handler.
        var result = await _sut.HandleAsync(new SearchProductsQuery("fruit"), CancellationToken.None);

        // ASSERT — DTOs round-trip with names and prices preserved.
        result.Should().BeSameAs(dtos);
        result.Select(r => r.Name).Should().Equal("Apple", "Banana");
        result.Select(r => r.Price).Should().Equal(1m, 2m);
    }

    [Fact]
    public async Task Handle_WhenNoMatches_ReturnsEmptyList()
    {
        // ARRANGE — Empty read-store result. Verifies the handler doesn't throw on empty
        // (e.g. by calling .First()) and returns an empty list rather than null.
        _readStore
            .SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ProductDto>());

        // ACT — Run the search with a query that won't match anything.
        var result = await _sut.HandleAsync(new SearchProductsQuery("zzz"), CancellationToken.None);

        // ASSERT — Returns an empty (non-null) list — null would force every API consumer
        // to handle a special case.
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForwardsPaginationParametersToReadStore()
    {
        // ARRANGE — Pagination is delegated to the read store (so the SQL has LIMIT/OFFSET).
        // If the handler ever started slicing in memory instead, this test would fail —
        // catching a perf regression (in-memory pagination loads all rows).
        _readStore
            .SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ProductDto>());

        // ACT — Request page 3, page size 25.
        var result = await _sut.HandleAsync(new SearchProductsQuery("anything", Page: 3, PageSize: 25), CancellationToken.None);

        // ASSERT — Read store received exactly those values + the empty result surfaces to
        // the caller (handler doesn't synthesize anything on top of the projection).
        await _readStore.Received(1).SearchAsync("anything", 3, 25, Arg.Any<CancellationToken>());
        result.Should().BeEmpty();
    }
}
