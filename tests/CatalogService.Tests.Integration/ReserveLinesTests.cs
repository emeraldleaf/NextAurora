using AwesomeAssertions;
using CatalogService.Domain;
using CatalogService.Features;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NextAurora.Contracts.DTOs;
using Wolverine;
using Xunit;

namespace CatalogService.Tests.Integration;

/// <summary>
/// Integration coverage for the batch order-placement methods (issue #71):
/// <see cref="ValidateLinesHandler"/> (one SQL query for the whole order) and
/// <see cref="ReserveLinesHandler"/> (atomic all-or-nothing reservation).
///
/// <para>
/// The load-bearing property proven here is <b>atomicity</b> — a reservation that fails on
/// ANY line must leave EVERY line's stock untouched. The old per-line ReserveStock fan-out
/// could leave lines 1–4 reserved when line 5 failed; that partial state required caller-side
/// compensation that never existed. These tests pin the "no partial outcome" contract over
/// real Postgres, where the single-SaveChanges transaction actually executes.
/// </para>
/// </summary>
public sealed class ReserveLinesTests(CatalogApiFactory factory) : IClassFixture<CatalogApiFactory>
{
    private readonly CatalogApiFactory _factory = factory;

    [Fact]
    public async Task ReserveLines_reserves_every_line_when_all_have_sufficient_stock()
    {
        // ARRANGE — Two products, both with enough stock for the requested quantities.
        var productA = await SeedProductAsync(stock: 10);
        var productB = await SeedProductAsync(stock: 5);

        // ACT — One batch command through Wolverine's pipeline (the same path the gRPC
        // ReserveLines endpoint takes via bus.InvokeAsync).
        var success = await InvokeReserveAsync([new ReserveLine(productA, 4), new ReserveLine(productB, 2)]);

        // ASSERT — Two invariants:
        //  1) The batch succeeded.
        //  2) BOTH stock decrements committed (10−4=6, 5−2=3) — the single transaction
        //     wrote every line, not just the first.
        success.Should().BeTrue();
        (await GetStockAsync(productA)).Should().Be(6);
        (await GetStockAsync(productB)).Should().Be(3);
    }

    [Fact]
    public async Task ReserveLines_is_atomic_one_insufficient_line_leaves_all_stock_untouched()
    {
        // ARRANGE — Product A could satisfy its line; product B cannot (stock 2, requested 5).
        // This is THE atomicity case: under the old per-line fan-out, A's reservation could
        // commit before B's failed, stranding reserved stock with no compensation path.
        var productA = await SeedProductAsync(stock: 10);
        var productB = await SeedProductAsync(stock: 2);

        // ACT — Batch reservation where line B must fail the stock check.
        var success = await InvokeReserveAsync([new ReserveLine(productA, 4), new ReserveLine(productB, 5)]);

        // ASSERT — Two invariants:
        //  1) The whole batch reports failure.
        //  2) Product A's stock is UNTOUCHED (still 10). This is the all-or-nothing
        //     guarantee — no partial state, nothing to compensate.
        success.Should().BeFalse();
        (await GetStockAsync(productA)).Should().Be(10);
        (await GetStockAsync(productB)).Should().Be(2);
    }

    [Fact]
    public async Task ReserveLines_fails_whole_batch_when_any_product_is_missing()
    {
        // ARRANGE — One real product, one ID that doesn't exist. A missing product must
        // fail the batch BEFORE any mutation (the caller's order references a product
        // that was deleted between validate and reserve).
        var productA = await SeedProductAsync(stock: 10);
        var missing = Guid.NewGuid();

        // ACT
        var success = await InvokeReserveAsync([new ReserveLine(productA, 1), new ReserveLine(missing, 1)]);

        // ASSERT — Failure, and the real product's stock is untouched.
        success.Should().BeFalse();
        (await GetStockAsync(productA)).Should().Be(10);
    }

    [Fact]
    public async Task ReserveLines_aggregates_duplicate_product_lines_before_the_stock_check()
    {
        // ARRANGE — The same product on two lines (qty 6 + qty 5 = 11 demanded, stock 10).
        // Checked per-line, each passes (6 ≤ 10, 5 ≤ 10); checked as aggregated demand,
        // the batch must fail. Without aggregation this would oversell by 1.
        var productA = await SeedProductAsync(stock: 10);

        // ACT
        var success = await InvokeReserveAsync([new ReserveLine(productA, 6), new ReserveLine(productA, 5)]);

        // ASSERT — The aggregated check rejected the batch; stock untouched.
        success.Should().BeFalse();
        (await GetStockAsync(productA)).Should().Be(10);
    }

    [Fact]
    public async Task ValidateLines_returns_only_existing_products_in_one_call()
    {
        // ARRANGE — Two real products and one unknown ID. The batch read must return the
        // two that exist and silently omit the missing one (absence = "not found" is the
        // contract OrderService's PlaceOrderHandler validates against).
        var productA = await SeedProductAsync(stock: 10);
        var productB = await SeedProductAsync(stock: 5);
        var missing = Guid.NewGuid();

        await using var scope = _factory.CreateDbScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        // ACT — One query for all three IDs.
        var products = await bus.InvokeAsync<List<ProductDto>>(
            new ValidateLinesQuery([productA, productB, missing]));

        // ASSERT — Exactly the two seeded products come back, with the server-controlled
        // price + stock the order handler needs.
        products.Should().HaveCount(2);
        products.Select(p => p.Id).Should().BeEquivalentTo([productA, productB]);
        products.Should().AllSatisfy(p => p.StockQuantity.Should().BeGreaterThan(0));
    }

    private async Task<bool> InvokeReserveAsync(List<ReserveLine> lines)
    {
        await using var scope = _factory.CreateDbScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        return await bus.InvokeAsync<bool>(new ReserveLinesCommand(lines));
    }

    private async Task<int> GetStockAsync(Guid productId)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.StockQuantity).SingleAsync();
    }

    private async Task<Guid> SeedProductAsync(int stock)
    {
        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var category = Category.Create("ReserveLines Test Category " + Guid.NewGuid(), "seeded by integration test");
        var product = Product.Create(
            name: "ReserveLines Test Product",
            description: "seeded by integration test",
            price: 9.99m,
            currency: "USD",
            categoryId: category.Id,
            sellerId: "test-seller",
            stockQuantity: stock);

        db.Categories.Add(category);
        db.Products.Add(product);
        await db.SaveChangesAsync();

        return product.Id;
    }
}
