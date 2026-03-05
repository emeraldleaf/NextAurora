using MediatR;
using NovaCraft.Contracts.DTOs;

namespace CatalogService.Application.Queries;

public record SearchProductsQuery(string Query) : IRequest<IReadOnlyList<ProductDto>>;
