using AwesomeAssertions;
using OrderService.Tests.Unit.Builders;

namespace OrderService.Tests.Unit.Domain;

public class OrderLineTests
{
    [Fact]
    public void Create_WithValidInputs_ReturnsOrderLine()
    {
        // ARRANGE — Builder produces a valid OrderLine (default quantity 1, price 9.99).
        // OrderLine is a child of the Order aggregate; like Order it has a private setter
        // pattern and a factory that validates.

        // ACT — Call the factory via the builder.
        var line = OrderLineBuilder.Default().Build();

        // ASSERT — Three invariants:
        //  1) Id is server-generated.
        //  2) ProductId carries through (every line points at a real Catalog product).
        //  3) Quantity is positive — zero/negative quantities are rejected by the factory
        //     (see the dedicated tests below).
        line.Id.Should().NotBeEmpty();
        line.ProductId.Should().NotBeEmpty();
        line.Quantity.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Create_WithEmptyProductId_ThrowsArgumentException()
    {
        // ARRANGE — A line with no ProductId can't be fulfilled (no stock to reserve, no
        // catalog entry to charge for). The factory rejects it up front.

        // ACT — Wrap so AwesomeAssertions can inspect the thrown exception.
        var act = () => OrderLineBuilder.Default().WithProductId(Guid.Empty).Build();

        // ASSERT — Parameter name lets callers find the field at fault.
        act.Should().Throw<ArgumentException>().WithParameterName("productId");
    }

    [Fact]
    public void Create_WithZeroQuantity_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE — Zero is rejected (use ThrowIfNegativeOrZero on quantity). Ordering
        // zero of something has no business meaning; if a buyer wants to remove a line,
        // they remove the line, not set its quantity to zero.

        // ACT — Wrap so AwesomeAssertions can inspect the thrown exception.
        var act = () => OrderLineBuilder.Default().WithQuantity(0).Build();

        // ASSERT — Domain rejects zero quantity.
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quantity");
    }

    [Fact]
    public void Create_WithNegativeQuantity_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE — Negative quantities are absurd; same rule as zero.

        // ACT — Wrap so AwesomeAssertions can inspect the thrown exception.
        var act = () => OrderLineBuilder.Default().WithQuantity(-1).Build();

        // ASSERT — Domain rejects negative quantity.
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quantity");
    }

    [Fact]
    public void Create_WithNegativePrice_ThrowsArgumentOutOfRangeException()
    {
        // ARRANGE — A negative unit price would compute a negative total (the buyer is
        // owed money for ordering) — clearly a config or input bug.

        // ACT — Wrap so AwesomeAssertions can inspect the thrown exception.
        var act = () => OrderLineBuilder.Default().WithUnitPrice(-1m).Build();

        // ASSERT — Domain rejects negative unit price.
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("unitPrice");
    }

    [Fact]
    public void Create_WithZeroPrice_Succeeds()
    {
        // ARRANGE — Zero IS allowed at the line level (OrderLine uses ThrowIfNegative,
        // not ThrowIfNegativeOrZero). This is intentional — it allows free promotional
        // items, gift-with-purchase lines, and "free shipping" pseudo-lines without
        // needing a coupon system. Documenting current behaviour so a future refactor
        // doesn't accidentally tighten this and break promo flows.

        // ACT — Call the factory via the builder.
        var line = OrderLineBuilder.Default().WithUnitPrice(0m).Build();

        // ASSERT — Zero price persists; promo lines stay valid.
        line.UnitPrice.Should().Be(0m);
    }
}
