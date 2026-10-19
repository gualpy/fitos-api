using FitOS.Application.Common;
using FitOS.Application.Sales;
using FitOS.Domain.Products;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FitOS.Infrastructure.Sales;

public sealed class SalesOptions
{
    public const string Section = "Sales";

    /// <summary>Payment method (cash, credit_card, ...) to the two-digit SRI code printed on the invoice.</summary>
    public Dictionary<string, string> PaymentCodes { get; set; } = [];
}

public sealed class ConfiguredPaymentCodes(IOptions<SalesOptions> options) : IPaymentCodes
{
    public string? CodeFor(string method) =>
        options.Value.PaymentCodes.TryGetValue(method, out var code) ? code : null;
}

public sealed class SaleRepository(FitOsDbContext db) : ISaleRepository
{
    public async Task<IReadOnlyList<Product>> FindProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);

    public async Task CreateAsync(SaleDraft sale, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var branchId = (await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM branches WHERE is_active ORDER BY name, id LIMIT 1")
            .ToListAsync(cancellationToken)).FirstOrDefault();
        if (branchId == Guid.Empty)
        {
            throw new ConflictException("no_branch");
        }

        var buyerKind = sale.CustomerId is null ? "final_consumer" : "identified";
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO sales (id, tenant_id, branch_id, customer_id, cashier_id, buyer_kind, subtotal, tax_total, total, status, created_at)
            VALUES ({sale.Id}, {sale.TenantId}, {branchId}, {sale.CustomerId}, {sale.CashierId}, {buyerKind},
                    {sale.Totals.Subtotal}, {sale.Totals.TaxTotal}, {sale.Totals.Total}, 'paid', {sale.CreatedAt})
            """,
            cancellationToken);

        // Always in the same order, so two sales sharing products cannot wait on each other forever.
        foreach (var line in sale.Lines.OrderBy(l => l.ProductId))
        {
            var taken = await db.Database.ExecuteSqlAsync(
                $"UPDATE products SET stock = stock - {line.Quantity} WHERE id = {line.ProductId} AND is_active AND stock >= {line.Quantity}",
                cancellationToken);
            if (taken != 1)
            {
                throw new ConflictException("insufficient_stock");
            }

            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO sale_lines (tenant_id, sale_id, product_id, description, quantity, unit_price, discount, iva_rate, line_total)
                VALUES ({sale.TenantId}, {sale.Id}, {line.ProductId}, {line.Description}, {line.Quantity}, {line.UnitPrice}, 0, {line.IvaRate}, {line.LineTotal})
                """,
                cancellationToken);

            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO stock_movements (tenant_id, product_id, delta, reason, sale_id, user_id) VALUES ({sale.TenantId}, {line.ProductId}, {-line.Quantity}, 'sale', {sale.Id}, {sale.CashierId})",
                cancellationToken);
        }

        foreach (var payment in sale.Payments)
        {
            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO sale_payments (tenant_id, sale_id, method, sri_code, amount) VALUES ({sale.TenantId}, {sale.Id}, {payment.Method}, {payment.SriCode}, {payment.Amount})",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
