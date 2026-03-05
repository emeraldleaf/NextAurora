using MediatR;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Queries;

public record SearchProductsQuery(string Query) : IRequest<IReadOnlyList<ProductDto>>;
