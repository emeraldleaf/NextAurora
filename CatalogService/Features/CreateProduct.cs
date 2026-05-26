using CatalogService.Domain;
using CatalogService.Infrastructure.Data;
using FluentValidation;

namespace CatalogService.Features;

/// <summary>
/// "Create product" vertical slice: command + validator + handler co-located. Seller-scoped
/// write — the endpoint enforces JWT-sub == <c>SellerId</c> before this handler ever runs
/// (see <c>CatalogEndpoints</c>). The handler trusts that check and persists the aggregate via
/// <c>CatalogDbContext</c> directly — no <c>IProductRepository</c> wrapper.
///
/// <para>
/// Per CLAUDE.md "Data access: DbContext directly, no repository wrappers": <c>DbContext</c>
/// is the Unit of Work; <c>DbSet&lt;T&gt;</c> is the Repository. Wrapping them in
/// <c>IProductRepository</c> added a layer without adding capability — and the only
/// substitution the wrapper enabled (unit tests mocking the repo) has been replaced with
/// integration tests against the real Postgres container in
/// <c>CatalogService.Tests.Integration</c>.
/// </para>
/// </summary>
public record CreateProductCommand(
    string Name,
    string Description,
    decimal Price,
    string Currency,
    Guid CategoryId,
    string SellerId,
    int StockQuantity);

public class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.Price).GreaterThan(0);
        RuleFor(x => x.Currency).NotEmpty().Length(3);
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.SellerId).NotEmpty();
        RuleFor(x => x.StockQuantity).GreaterThanOrEqualTo(0);
    }
}

public class CreateProductHandler(CatalogDbContext context)
{
    public async Task<Guid> HandleAsync(CreateProductCommand request, CancellationToken cancellationToken)
    {
        var product = Product.Create(
            request.Name, request.Description, request.Price,
            request.Currency, request.CategoryId, request.SellerId, request.StockQuantity);

        await context.Products.AddAsync(product, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return product.Id;
    }
}
