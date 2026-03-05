using MediatR;
using NovaCraft.Contracts.DTOs;

namespace CatalogService.Application.Queries;

public record GetProductByIdQuery(Guid ProductId) : IRequest<ProductDto?>;
