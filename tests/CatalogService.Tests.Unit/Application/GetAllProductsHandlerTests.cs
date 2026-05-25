using AwesomeAssertions;
using CatalogService.Application.Handlers;
using CatalogService.Application.Interfaces;
using CatalogService.Application.Queries;
using NextAurora.Contracts.DTOs;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class GetAllProductsHandlerTests
{
    private readonly IProductReadStore _readStore = Substitute.For<IProductReadStore>();
    private readonly GetAllProductsHandler _sut;

    public GetAllProductsHandlerTests()
    {
        _sut = new GetAllProductsHandler(_readStore);
    }

    [Fact]
    public async Task Handle_WhenProductsExist_ReturnsDtosFromReadStore()
    {
        // ARRANGE — The handler is a one-line passthrough to IProductReadStore.GetAllAsync,
        // which projects in EF (AsNoTracking + Select) and returns the DTO directly. No entity
        // hop, no in-memory mapper — see docs/cqrs-data-access.md.
        var dtos = new List<ProductDto>
        {
            new() { Id = Guid.NewGuid(), Name = "A", Price = 1m },
            new() { Id = Guid.NewGuid(), Name = "B", Price = 2m }
        };
        _readStore.GetAllAsync(1, 50, Arg.Any<CancellationToken>()).Returns(dtos);

        // ACT — Run the handler against the default query.
        var result = await _sut.HandleAsync(new GetAllProductsQuery(), CancellationToken.None);

        // ASSERT — DTOs pass through unchanged.
        result.Should().BeSameAs(dtos);
        result.Select(r => r.Name).Should().Equal("A", "B");
    }

    [Fact]
    public async Task Handle_WhenEmpty_ReturnsEmptyList()
    {
        // ARRANGE — No products. Verifies the handler doesn't crash on empty.
        _readStore
            .GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ProductDto>());

        // ACT — Run the handler against the default query.
        var result = await _sut.HandleAsync(new GetAllProductsQuery(), CancellationToken.None);

        // ASSERT — Non-null, empty list (never null collections from queries).
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForwardsPaginationParametersToReadStore()
    {
        // ARRANGE — The 100-row CLAUDE.md cap is enforced upstream (endpoint validators),
        // not here. The handler must pass through whatever it gets. This test guards against
        // a future "in-memory pagination" regression — if someone removes the LIMIT/OFFSET
        // delegation, the page/size args would no longer reach the read store and this fails.
        _readStore
            .GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<ProductDto>());

        // ACT — Page 5, size 20.
        var result = await _sut.HandleAsync(new GetAllProductsQuery(Page: 5, PageSize: 20), CancellationToken.None);

        // ASSERT — Pagination args flow straight through to the read store + the empty result
        // surfaces to the caller (handler doesn't synthesize anything on top of the projection).
        await _readStore.Received(1).GetAllAsync(5, 20, Arg.Any<CancellationToken>());
        result.Should().BeEmpty();
    }
}
