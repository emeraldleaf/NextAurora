namespace CatalogService.Domain;

/// <summary>
/// A product category — a simple bucket products belong to. Lighter than <see cref="Product"/>:
/// no rich invariants, no state transitions. Categories are mostly a reference table.
///
/// <para>
/// Note <c>Products</c> is exposed as a mutable <see cref="List{T}"/> here, not <c>IReadOnlyList</c>.
/// That's because Category isn't an aggregate root that owns Products — Product is its own
/// aggregate root, and the inverse navigation only exists so EF Core can populate it via
/// <c>Include(p =&gt; p.Category)</c> in the other direction. Don't add products to a category
/// through this collection in application code; create products with the category ID instead.
/// </para>
/// </summary>
public class Category
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Description { get; private set; } = "";
    public List<Product> Products { get; private set; } = [];

    private Category() { }

    public static Category Create(string name, string description)
    {
        return new Category
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description
        };
    }
}
