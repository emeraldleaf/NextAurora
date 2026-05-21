using CatalogService.Domain.Entities;
using NextAurora.Contracts.DTOs;

namespace CatalogService.Application.Mappers;

/// <summary>
/// Single source of truth for <see cref="Product"/> → <see cref="ProductDto"/> projection.
/// Three handlers (<c>GetAllProductsHandler</c>, <c>SearchProductsHandler</c>,
/// <c>GetProductByIdHandler</c>) previously open-coded the same property copy; if a new field
/// appeared on <c>ProductDto</c>, it had to be added in three places. Centralized here so that
/// schema evolution touches one file.
/// </summary>
internal static class ProductMapper
{
    public static ProductDto ToDto(Product product) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Description = product.Description,
        Price = product.Price,
        Currency = product.Currency,
        Category = product.Category?.Name ?? "",
        SellerId = product.SellerId,
        StockQuantity = product.StockQuantity,
        IsAvailable = product.IsAvailable
    };
}
