using CatalogService.Domain.Entities;
using CatalogService.Domain.Interfaces;
using CatalogService.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CatalogService.Infrastructure.Repositories;

public class CategoryRepository(CatalogDbContext context) : ICategoryRepository
{
    public async Task<Category?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await context.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct = default)
        => await context.Categories.AsNoTracking().ToListAsync(ct);

    public async Task AddAsync(Category category, CancellationToken ct = default)
    {
        await context.Categories.AddAsync(category, ct);
        await context.SaveChangesAsync(ct);
    }
}
