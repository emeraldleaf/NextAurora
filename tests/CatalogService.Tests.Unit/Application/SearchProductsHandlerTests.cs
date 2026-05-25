using AwesomeAssertions;
using CatalogService.Application.Handlers;
using CatalogService.Application.Queries;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Tests.Unit.Builders;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class SearchProductsHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly SearchProductsHandler _sut;

    public SearchProductsHandlerTests()
    {
        _sut = new SearchProductsHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenRepositoryReturnsProducts_MapsToDtos()
    {
        // ARRANGE — The handler is thin (delegate + map). The interesting behaviour is the
        // mapping: each Product comes back as a ProductDto with the projection-relevant
        // fields populated. We seed two products and check both make the round trip.
        var p1 = ProductBuilder.Default().WithName("Apple").WithPrice(1m).Build();
        var p2 = ProductBuilder.Default().WithName("Banana").WithPrice(2m).Build();
        _repository
            .SearchAsync("fruit", 1, 50, Arg.Any<CancellationToken>())
            .Returns(new List<Product> { p1, p2 });

        // ACT — Run the search query through the handler.
        var result = await _sut.HandleAsync(new SearchProductsQuery("fruit"), CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) The count matches what the repo returned (no filtering happens in the handler).
        //  2) Field-level mapping is correct — names and prices survive. If a future schema
        //     change adds a field on ProductDto, the central ProductMapper would also need
        //     updating; tests for the dedicated mapper (or this test if extended) catch it.
        result.Should().HaveCount(2);
        result.Select(r => r.Name).Should().Equal("Apple", "Banana");
        result.Select(r => r.Price).Should().Equal(1m, 2m);
    }

    [Fact]
    public async Task Handle_WhenNoMatches_ReturnsEmptyList()
    {
        // ARRANGE — Empty repository result. Important to verify the handler doesn't throw
        // on empty (e.g. by calling .First()) and returns an empty list rather than null.
        _repository
            .SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product>());

        // ACT — Run the search with a query that won't match anything.
        var result = await _sut.HandleAsync(new SearchProductsQuery("zzz"), CancellationToken.None);

        // ASSERT — Returns an empty (non-null) list — null would force every API consumer
        // to handle a special case.
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForwardsPaginationParametersToRepository()
    {
        // ARRANGE — Pagination is delegated to the repository (so the SQL has LIMIT/OFFSET).
        // If the handler ever started slicing in memory instead, this test would fail —
        // catching a perf regression (in-memory pagination loads all rows).
        _repository
            .SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product>());

        // ACT — Request page 3, page size 25.
        await _sut.HandleAsync(new SearchProductsQuery("anything", Page: 3, PageSize: 25), CancellationToken.None);

        // ASSERT — Repository received exactly those values.
        await _repository.Received(1).SearchAsync("anything", 3, 25, Arg.Any<CancellationToken>());
    }
}
