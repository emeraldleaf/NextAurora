using AwesomeAssertions;
using CatalogService.Application.Handlers;
using CatalogService.Application.Queries;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Tests.Unit.Builders;
using NSubstitute;

namespace CatalogService.Tests.Unit.Application;

public class GetAllProductsHandlerTests
{
    private readonly IProductRepository _repository = Substitute.For<IProductRepository>();
    private readonly GetAllProductsHandler _sut;

    public GetAllProductsHandlerTests()
    {
        _sut = new GetAllProductsHandler(_repository);
    }

    [Fact]
    public async Task Handle_WhenProductsExist_ReturnsMappedDtos()
    {
        // ARRANGE — Two real Products; the repo returns them.
        var p1 = ProductBuilder.Default().WithName("A").Build();
        var p2 = ProductBuilder.Default().WithName("B").Build();
        _repository
            .GetAllAsync(1, 50, Arg.Any<CancellationToken>())
            .Returns(new List<Product> { p1, p2 });

        // ACT
        var result = await _sut.HandleAsync(new GetAllProductsQuery(), CancellationToken.None);

        // ASSERT — Round-trip count + names. The DTO shape is owned by ProductMapper;
        // we only check the projection actually happens (not null, count preserved).
        result.Should().HaveCount(2);
        result.Select(r => r.Name).Should().Equal("A", "B");
    }

    [Fact]
    public async Task Handle_WhenEmpty_ReturnsEmptyList()
    {
        // ARRANGE — No products. Mostly verifies the handler doesn't crash on empty.
        _repository
            .GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product>());

        // ACT
        var result = await _sut.HandleAsync(new GetAllProductsQuery(), CancellationToken.None);

        // ASSERT
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForwardsPaginationParametersToRepository()
    {
        // ARRANGE — The 100-row CLAUDE.md cap is enforced upstream (endpoint validators),
        // not here. The handler must pass through whatever it gets. This test guards against
        // a future "in-memory pagination" regression — if someone removes the LIMIT/OFFSET
        // delegation, the page/size args would no longer reach the repo and this fails.
        _repository
            .GetAllAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Product>());

        // ACT — Page 5, size 20.
        await _sut.HandleAsync(new GetAllProductsQuery(Page: 5, PageSize: 20), CancellationToken.None);

        // ASSERT
        await _repository.Received(1).GetAllAsync(5, 20, Arg.Any<CancellationToken>());
    }
}
