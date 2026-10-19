using FitOS.Application.Common;
using FitOS.Application.Products;
using FitOS.Domain.Products;
using FitOS.Infrastructure.Members;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Products;

public sealed class ProductRepository(FitOsDbContext db) : IProductRepository
{
    private const int MaxRows = 500;

    private static readonly Dictionary<string, string> Constraints = new()
    {
        ["products_name_uq"] = "product_name_taken",
        ["products_sku_uq"] = "sku_taken",
        ["products_barcode_uq"] = "barcode_taken",
    };

    public async Task<IReadOnlyList<Product>> ListAsync(string? text, bool includeInactive, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking().Where(p => includeInactive || p.IsActive);

        if (text is not null)
        {
            var like = $"%{text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(p =>
                EF.Functions.ILike(p.Name, like, "\\")
                || (p.Sku != null && EF.Functions.ILike(p.Sku, like, "\\"))
                || (p.Barcode != null && EF.Functions.ILike(p.Barcode, like, "\\"))
                || (p.Category != null && EF.Functions.ILike(p.Category, like, "\\")));
        }

        return await query.OrderBy(p => p.Name.ToLower()).Take(MaxRows).ToListAsync(cancellationToken);
    }

    public Task<Product?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Product?> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task CreateAsync(Product product, Guid? userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Products.Add(product);
        await SaveHelper.SaveAsync(db, Constraints, cancellationToken);

        if (product.Stock > 0)
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO stock_movements (tenant_id, product_id, delta, reason, user_id) VALUES ({product.TenantId}, {product.Id}, {product.Stock}, 'adjustment', {userId})",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task SaveAsync(CancellationToken cancellationToken) => SaveHelper.SaveAsync(db, Constraints, cancellationToken);

    public async Task<bool> AdjustStockAsync(Guid productId, int delta, string reason, Guid? userId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // One statement checks and moves: two cashiers cannot both take the last unit.
        var moved = await db.Database.ExecuteSqlAsync(
            $"UPDATE products SET stock = stock + {delta} WHERE id = {productId} AND stock + {delta} >= 0",
            cancellationToken);
        if (moved != 1)
        {
            return false;
        }

        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO stock_movements (tenant_id, product_id, delta, reason, user_id) SELECT tenant_id, id, {delta}, {reason}, {userId} FROM products WHERE id = {productId}",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
