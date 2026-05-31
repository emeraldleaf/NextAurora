using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CatalogService.Infrastructure.Data;

/// <summary>
/// Used by `dotnet ef migrations add` and other EF Core CLI tooling. Not used at runtime —
/// the Aspire-injected connection string flows through DI instead.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__catalog-db")
            ?? "Host=localhost;Database=catalog-db;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(cs).Options;
        return new CatalogDbContext(options);
    }
}
