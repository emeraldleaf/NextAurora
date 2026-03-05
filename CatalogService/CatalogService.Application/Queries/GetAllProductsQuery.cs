using MediatR;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Queries;

public record GetAllProductsQuery : IRequest<IReadOnlyList<ProductDto>>;
