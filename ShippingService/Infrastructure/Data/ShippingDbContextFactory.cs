using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShippingService.Infrastructure.Data;

/// <summary>
/// Used by `dotnet ef migrations add` and other EF Core CLI tooling. Not used at runtime —
/// the Aspire-injected connection string flows through DI instead.
/// </summary>
public sealed class ShippingDbContextFactory : IDesignTimeDbContextFactory<ShippingDbContext>
{
    public ShippingDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__shipping-db")
            ?? "Host=localhost;Database=shipping-db;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<ShippingDbContext>().UseNpgsql(cs).Options;
        return new ShippingDbContext(options);
    }
}
