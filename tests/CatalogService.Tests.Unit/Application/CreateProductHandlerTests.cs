using AwesomeAssertions;
using CatalogService.Application.Commands;
using CatalogService.Application.Handlers;
using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
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

    private static CreateProductCommand ValidCommand() =>
        new("Widget", "A useful widget", 19.99m, "USD", Guid.NewGuid(), "seller-1", 10);

    [Fact]
    public async Task Handle_WithValidCommand_CreatesProductAndReturnsId()
    {
        // ARRANGE — Build a valid command. The handler delegates to Product.Create (the
        // aggregate's only construction chokepoint) and then saves the result.
        var command = ValidCommand();

        // ACT — Run the handler.
        var result = await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Two invariants:
        //  1) Returns the server-generated GUID (CLAUDE.md "Key Conventions" — commands
        //     return the created entity's ID, never a DTO or the entity itself).
        //  2) Repository.AddAsync was called exactly once with a Product whose Id matches
        //     what we returned — proves we didn't swallow the entity or return a stale ID.
        result.Should().NotBeEmpty();
        await _repository.Received(1).AddAsync(
            Arg.Is<Product>(p => p.Id == result),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PersistsAllCommandFieldsOnTheAggregate()
    {
        // ARRANGE — Same valid command, but we'll verify the Product field-by-field. This
        // protects against a refactor that accidentally drops a field from the factory
        // call (e.g. a copy-paste that swaps CategoryId for SellerId).
        var categoryId = Guid.NewGuid();
        var command = new CreateProductCommand(
            "Doohickey", "A doohickey", 42m, "USD", categoryId, "seller-2", 7);

        // ACT — Run the handler.
        await _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — The persisted aggregate carries every field we sent. We use NSubstitute's
        // Arg.Is predicate to pattern-match a single AddAsync call.
        await _repository.Received(1).AddAsync(
            Arg.Is<Product>(p =>
                p.Name == "Doohickey" &&
                p.Description == "A doohickey" &&
                p.Price == 42m &&
                p.Currency == "USD" &&
                p.CategoryId == categoryId &&
                p.SellerId == "seller-2" &&
                p.StockQuantity == 7 &&
                p.IsAvailable),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Handle_WithEmptyName_BubblesUpDomainValidationException(string invalidName)
    {
        // ARRANGE — Product.Create rejects empty/whitespace names. The handler doesn't
        // re-validate (no duplication of domain rules) — it lets the exception bubble.
        // We assert the exception type rather than swallowing it, because the API's
        // GlobalExceptionHandler decides the HTTP shape. (CLAUDE.md DDD: factory methods
        // own validation; handlers don't restate it.)
        var command = ValidCommand() with { Name = invalidName };

        // ACT — Wrap so AwesomeAssertions can inspect the thrown exception.
        var act = () => _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — The domain's ArgumentException surfaces. Crucially, AddAsync is NOT
        // called — a failed creation never reaches persistence.
        await act.Should().ThrowAsync<ArgumentException>();
        await _repository.DidNotReceive().AddAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithNonPositivePrice_BubblesUpDomainValidationException()
    {
        // ARRANGE — Product.Create requires Price > 0 (a $0 product is almost always a
        // config bug; "free" should be a coupon, not a list price). Verify a 0 price
        // is refused.
        var command = ValidCommand() with { Price = 0m };

        // ACT — Wrap so AwesomeAssertions can inspect the thrown exception.
        var act = () => _sut.HandleAsync(command, CancellationToken.None);

        // ASSERT — Domain rejects price=0; no persistence.
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await _repository.DidNotReceive().AddAsync(Arg.Any<Product>(), Arg.Any<CancellationToken>());
    }
}
