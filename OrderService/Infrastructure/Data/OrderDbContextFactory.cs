using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OrderService.Infrastructure.Data;

/// <summary>
/// Used by `dotnet ef migrations add` and other EF Core CLI tooling. Not used at runtime —
/// the Aspire-injected connection string flows through DI instead.
/// </summary>
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    public OrderDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__orders-db")
            ?? "Server=localhost;Database=orders-db;Trusted_Connection=False;User Id=sa;Password=Your_password123;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<OrderDbContext>().UseSqlServer(cs).Options;
        return new OrderDbContext(options);
    }
}
