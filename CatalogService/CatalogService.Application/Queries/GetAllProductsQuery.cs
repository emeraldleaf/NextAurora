using MediatR;
using NovaCraft.Contracts.DTOs;

namespace CatalogService.Application.Queries;

public record GetAllProductsQuery : IRequest<IReadOnlyList<ProductDto>>;
