namespace CatalogService.Application.Queries;

public record GetAllProductsQuery(int Page = 1, int PageSize = 50);
