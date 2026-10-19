namespace FitOS.Domain.Products;

/// <summary>An item the gym sells over the counter. Prices are WITHOUT IVA. Products are deactivated, never deleted: sales reference them.</summary>
public sealed class Product
{
    public static readonly IReadOnlyList<decimal> AllowedIvaRates = [0m, 5m, 15m];

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public string? Sku { get; set; }

    public string? Barcode { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Category { get; set; }

    public decimal Price { get; set; }

    public decimal IvaRate { get; set; } = 15m;

    /// <summary>Units on the shelf. Changes only through stock movements (sales, purchases, adjustments).</summary>
    public int Stock { get; set; }

    public bool IsActive { get; set; } = true;
}
