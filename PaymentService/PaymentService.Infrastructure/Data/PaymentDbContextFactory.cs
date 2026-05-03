using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PaymentService.Infrastructure.Data;

/// <summary>
/// Used by `dotnet ef migrations add` and other EF Core CLI tooling. Not used at runtime —
/// the Aspire-injected connection string flows through DI instead.
/// </summary>
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__payments-db")
            ?? "Server=localhost;Database=payments-db;Trusted_Connection=False;User Id=sa;Password=Your_password123;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<PaymentDbContext>().UseSqlServer(cs).Options;
        return new PaymentDbContext(options);
    }
}
