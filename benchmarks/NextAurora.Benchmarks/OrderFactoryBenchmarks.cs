using BenchmarkDotNet.Attributes;
using OrderService.Domain.Entities;

namespace NextAurora.Benchmarks;

/// <summary>
/// Starter benchmark — exercises the <see cref="Order"/> domain factory with varying line
/// counts. Picked as the first benchmark because:
/// <list type="bullet">
///   <item>Pure domain code, no DI, no DB, no async — produces clean, repeatable numbers.</item>
///   <item>Order placement is on the hot path (every checkout). Regressions matter.</item>
///   <item>Demonstrates the harness on a single concrete piece of code; anyone adding new
///         benchmarks can copy this shape.</item>
/// </list>
///
/// Run from repo root:
/// <code>
/// dotnet run -c Release --project benchmarks/NextAurora.Benchmarks -- --filter '*OrderFactory*'
/// </code>
/// </summary>
[MemoryDiagnoser]
public class OrderFactoryBenchmarks
{
    [Params(1, 5, 25)]
    public int LineCount;

    private readonly Guid _buyerId = Guid.NewGuid();
    private List<OrderLine> _lines = null!;

    [GlobalSetup]
    public void Setup()
    {
        _lines = new List<OrderLine>(LineCount);
        for (var i = 0; i < LineCount; i++)
        {
            _lines.Add(OrderLine.Create(
                Guid.NewGuid(),
                $"Product {i}",
                quantity: 1,
                unitPrice: 9.99m));
        }
    }

    [Benchmark]
    public Order CreateOrder() => Order.Create(_buyerId, "USD", _lines);
}
