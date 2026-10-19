using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Api.Products;
using FitOS.Application.Sales;

namespace FitOS.Api.Sales;

/// <summary>Counter sales of the caller's gym. Prices and taxes are always computed by the server.</summary>
public static class SalesEndpoints
{
    public static IEndpointRouteBuilder MapSales(this IEndpointRouteBuilder app)
    {
        app.MapPost("/sales", Create).RequireAuthorization(AuthPolicies.Staff);
        return app;
    }

    private static async Task<IResult> Create(SaleRequest body, ClaimsPrincipal user, SaleService service, CancellationToken ct)
    {
        var input = new SaleInput(
            body.CustomerId,
            body.Lines?.Select(l => new SaleLineInput(l.ProductId, l.Quantity)).ToList(),
            body.Payments?.Select(p => new PaymentInput(p.Method, p.Amount)).ToList());

        var sale = await service.CreateAsync(input, ProductsEndpoints.UserId(user), ct);
        return Results.Created($"/sales/{sale.Id}", new
        {
            sale.Id,
            sale.CustomerId,
            sale.CreatedAt,
            sale.Totals.Subtotal,
            sale.Totals.TaxTotal,
            sale.Totals.Total,
            Lines = sale.Lines.Select(l => new { l.ProductId, l.Description, l.Quantity, l.UnitPrice, l.IvaRate, l.LineTotal }),
            Payments = sale.Payments.Select(p => new { p.Method, p.Amount }),
        });
    }

    private sealed record SaleRequest(Guid? CustomerId, List<LineRequest>? Lines, List<PaymentRequest>? Payments);

    private sealed record LineRequest(Guid? ProductId, int? Quantity);

    private sealed record PaymentRequest(string? Method, decimal? Amount);
}
