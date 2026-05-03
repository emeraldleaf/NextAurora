namespace CatalogService.Application.Queries;

public record SearchProductsQuery(string Query, int Page = 1, int PageSize = 50);
