using System.Globalization;
using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Application.Products;
using FitOS.Domain.Products;

namespace FitOS.Api.Products;

/// <summary>Catalogue and stock of the caller's gym. The gym comes from the token, never from the URL.</summary>
public static class ProductsEndpoints
{
    public static IEndpointRouteBuilder MapProducts(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/products");
        group.MapGet("/", List).RequireAuthorization(AuthPolicies.Staff);
        group.MapGet("/{id:guid}", Get).RequireAuthorization(AuthPolicies.Staff);
        group.MapPost("/", Create).RequireAuthorization(AuthPolicies.Manager);
        group.MapPut("/{id:guid}", Update).RequireAuthorization(AuthPolicies.Manager);
        group.MapPut("/{id:guid}/active", SetActive).RequireAuthorization(AuthPolicies.Manager);
        group.MapPost("/{id:guid}/stock", AdjustStock).RequireAuthorization(AuthPolicies.Manager);
        return app;
    }

    private static async Task<IResult> List(string? q, bool? includeInactive, ProductService service, CancellationToken ct) =>
        Results.Ok((await service.ListAsync(q, includeInactive == true, ct)).Select(ToResponse));

    private static async Task<IResult> Get(Guid id, ProductService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.GetAsync(id, ct)));

    private static async Task<IResult> Create(ProductRequest body, ClaimsPrincipal user, ProductService service, CancellationToken ct)
    {
        var product = await service.CreateAsync(ToInput(body), UserId(user), ct);
        return Results.Created($"/products/{product.Id}", ToResponse(product));
    }

    private static async Task<IResult> Update(Guid id, ProductRequest body, ProductService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.UpdateAsync(id, ToInput(body), ct)));

    private static async Task<IResult> SetActive(Guid id, ActiveRequest body, ProductService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.SetActiveAsync(id, body.IsActive, ct)));

    private static async Task<IResult> AdjustStock(Guid id, StockRequest body, ClaimsPrincipal user, ProductService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.AdjustStockAsync(id, new StockAdjustmentInput(body.Delta, body.Reason), UserId(user), ct)));

    internal static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    private static ProductInput ToInput(ProductRequest r) => new(r.Sku, r.Barcode, r.Name, r.Category, r.Price, r.IvaRate, r.Stock);

    private static object ToResponse(Product p) => new
    {
        p.Id,
        p.Sku,
        p.Barcode,
        p.Name,
        p.Category,
        p.Price,
        p.IvaRate,
        p.Stock,
        p.IsActive,
    };

    private sealed record ProductRequest(string? Sku, string? Barcode, string? Name, string? Category, decimal? Price, decimal? IvaRate, int? Stock);

    private sealed record ActiveRequest(bool IsActive);

    private sealed record StockRequest(int? Delta, string? Reason);
}
