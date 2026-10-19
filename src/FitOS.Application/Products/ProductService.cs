using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Domain.Products;

namespace FitOS.Application.Products;

public sealed record ProductInput(string? Sku, string? Barcode, string? Name, string? Category, decimal? Price, decimal? IvaRate, int? Stock);

public sealed record StockAdjustmentInput(int? Delta, string? Reason);

public interface IProductRepository
{
    Task<IReadOnlyList<Product>> ListAsync(string? text, bool includeInactive, CancellationToken cancellationToken);

    /// <summary>Tracked, so it can be edited and saved.</summary>
    Task<Product?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Not tracked: always the row as it is in the database now.</summary>
    Task<Product?> ReadAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Adds the product and, when it starts with stock, the movement that explains it.</summary>
    /// <exception cref="ConflictException"><c>product_name_taken</c>, <c>sku_taken</c> or <c>barcode_taken</c></exception>
    Task CreateAsync(Product product, Guid? userId, CancellationToken cancellationToken);

    /// <exception cref="ConflictException"><c>product_name_taken</c>, <c>sku_taken</c> or <c>barcode_taken</c></exception>
    Task SaveAsync(CancellationToken cancellationToken);

    /// <summary>Moves stock and records why. <c>false</c> when it would leave less than zero (nothing is written).</summary>
    Task<bool> AdjustStockAsync(Guid productId, int delta, string reason, Guid? userId, CancellationToken cancellationToken);
}

public sealed class ProductService(IProductRepository products, ITenantContext tenant)
{
    public const int MaxStock = 1_000_000;

    private static readonly string[] AdjustmentReasons = ["adjustment", "purchase"];

    public Task<IReadOnlyList<Product>> ListAsync(string? text, bool includeInactive, CancellationToken cancellationToken) =>
        products.ListAsync(string.IsNullOrWhiteSpace(text) ? null : text.Trim(), includeInactive, cancellationToken);

    public async Task<Product> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await products.ReadAsync(id, cancellationToken) ?? throw new NotFoundException("product");

    public async Task<Product> CreateAsync(ProductInput input, Guid? userId, CancellationToken cancellationToken)
    {
        var product = new Product { TenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope.") };
        var errors = new ErrorBag();
        Apply(product, input, errors);

        var stock = input.Stock ?? 0;
        if (stock is < 0 or > MaxStock)
        {
            errors.Add("stock", "invalid");
        }

        errors.ThrowIfAny();

        product.Stock = stock;
        await products.CreateAsync(product, userId, cancellationToken);
        return product;
    }

    public async Task<Product> UpdateAsync(Guid id, ProductInput input, CancellationToken cancellationToken)
    {
        var product = await products.GetAsync(id, cancellationToken) ?? throw new NotFoundException("product");
        var errors = new ErrorBag();
        Apply(product, input, errors);
        errors.ThrowIfAny();

        await products.SaveAsync(cancellationToken);
        return product;
    }

    public async Task<Product> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var product = await products.GetAsync(id, cancellationToken) ?? throw new NotFoundException("product");
        product.IsActive = isActive;
        await products.SaveAsync(cancellationToken);
        return product;
    }

    public async Task<Product> AdjustStockAsync(Guid id, StockAdjustmentInput input, Guid? userId, CancellationToken cancellationToken)
    {
        var errors = new ErrorBag();
        if (input.Delta is null or 0 || Math.Abs((long)input.Delta.Value) > MaxStock)
        {
            errors.Add("delta", input.Delta is null ? "required" : "invalid");
        }

        var reason = input.Reason?.Trim().ToLowerInvariant();
        if (reason is null || !AdjustmentReasons.Contains(reason))
        {
            errors.Add("reason", reason is null ? "required" : "invalid");
        }

        errors.ThrowIfAny();

        _ = await products.ReadAsync(id, cancellationToken) ?? throw new NotFoundException("product");

        if (!await products.AdjustStockAsync(id, input.Delta!.Value, reason!, userId, cancellationToken))
        {
            throw new ConflictException("insufficient_stock");
        }

        return await products.ReadAsync(id, cancellationToken) ?? throw new NotFoundException("product");
    }

    private static void Apply(Product product, ProductInput input, ErrorBag errors)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors.Add("name", "required");
        }
        else if (name.Length > 100)
        {
            errors.Add("name", "too_long");
        }

        var sku = Clean(input.Sku);
        if (sku is { Length: > 50 })
        {
            errors.Add("sku", "too_long");
        }

        var barcode = Clean(input.Barcode);
        if (barcode is { Length: > 50 })
        {
            errors.Add("barcode", "too_long");
        }

        var category = Clean(input.Category);
        if (category is { Length: > 50 })
        {
            errors.Add("category", "too_long");
        }

        if (input.Price is not { } price)
        {
            errors.Add("price", "required");
        }
        else if (price is < 0 or > 100000 || decimal.Round(price, 2) != price)
        {
            errors.Add("price", "invalid");
        }

        var iva = input.IvaRate ?? 15m;
        if (!Product.AllowedIvaRates.Contains(iva))
        {
            errors.Add("ivaRate", "invalid");
        }

        if (name is null || errors.HasAny)
        {
            return;
        }

        product.Name = name;
        product.Sku = sku;
        product.Barcode = barcode;
        product.Category = category;
        product.Price = input.Price!.Value;
        product.IvaRate = iva;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
