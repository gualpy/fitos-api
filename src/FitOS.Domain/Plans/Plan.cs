namespace FitOS.Domain.Plans;

/// <summary>A membership plan. Prices are WITHOUT IVA. Plans are deactivated, never deleted: sales reference them.</summary>
public sealed class Plan
{
    public static readonly IReadOnlyList<decimal> AllowedIvaRates = [0m, 5m, 15m];

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal Price { get; set; }

    public decimal IvaRate { get; set; } = 15m;

    public int DurationMonths { get; set; }

    public bool IsPromotion { get; set; }

    public bool IsActive { get; set; } = true;
}
