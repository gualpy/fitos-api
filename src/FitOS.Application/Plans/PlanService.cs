using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Domain.Plans;

namespace FitOS.Application.Plans;

public sealed record PlanInput(string? Name, string? Description, decimal? Price, decimal? IvaRate, int? DurationMonths, bool? IsPromotion);

public interface IPlanRepository
{
    Task<IReadOnlyList<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken);

    Task<Plan?> GetAsync(Guid id, CancellationToken cancellationToken);

    void Add(Plan plan);

    /// <exception cref="ConflictException"><c>plan_name_taken</c></exception>
    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed class PlanService(IPlanRepository plans, ITenantContext tenant)
{
    public Task<IReadOnlyList<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
        plans.ListAsync(includeInactive, cancellationToken);

    public async Task<Plan> GetAsync(Guid id, bool includeInactive, CancellationToken cancellationToken)
    {
        var plan = await plans.GetAsync(id, cancellationToken);
        return plan is null || (!plan.IsActive && !includeInactive) ? throw new NotFoundException("plan") : plan;
    }

    public async Task<Plan> CreateAsync(PlanInput input, CancellationToken cancellationToken)
    {
        var plan = new Plan
        {
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope."),
        };
        Apply(plan, input, creating: true);

        plans.Add(plan);
        await plans.SaveAsync(cancellationToken);
        return plan;
    }

    public async Task<Plan> UpdateAsync(Guid id, PlanInput input, CancellationToken cancellationToken)
    {
        var plan = await plans.GetAsync(id, cancellationToken) ?? throw new NotFoundException("plan");
        Apply(plan, input, creating: false);

        await plans.SaveAsync(cancellationToken);
        return plan;
    }

    public async Task<Plan> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var plan = await plans.GetAsync(id, cancellationToken) ?? throw new NotFoundException("plan");
        plan.IsActive = isActive;
        await plans.SaveAsync(cancellationToken);
        return plan;
    }

    private static void Apply(Plan plan, PlanInput input, bool creating)
    {
        var errors = new ErrorBag();

        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors.Add("name", "required");
        }
        else if (name.Length > 100)
        {
            errors.Add("name", "too_long");
        }

        if (input.Description is { Length: > 500 })
        {
            errors.Add("description", "too_long");
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
        if (!Plan.AllowedIvaRates.Contains(iva))
        {
            errors.Add("ivaRate", "invalid");
        }

        if (input.DurationMonths is not (>= 1 and <= 60))
        {
            errors.Add("durationMonths", input.DurationMonths is null ? "required" : "invalid");
        }

        errors.ThrowIfAny();

        plan.Name = name!;
        plan.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        plan.Price = input.Price!.Value;
        plan.IvaRate = iva;
        plan.DurationMonths = input.DurationMonths!.Value;
        plan.IsPromotion = input.IsPromotion ?? (creating ? false : plan.IsPromotion);
    }
}
