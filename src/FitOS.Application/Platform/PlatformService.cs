using FitOS.Application.Auth;
using FitOS.Application.Common;

namespace FitOS.Application.Platform;

public sealed record TenantSummary(
    Guid Id,
    string Name,
    string Slug,
    string Status,
    string EffectiveStatus,
    DateOnly SubscriptionEndsOn,
    int DaysLeft,
    DateTimeOffset CreatedAt,
    long Customers);

public sealed record MonthlyRevenue(DateOnly Month, decimal Total, long Payments);

public sealed record TenantOwner(Guid UserId, string Username, string DisplayName, string? Email, bool MustChangePassword, string GymName, string GymSlug);

public interface IPlatformStore
{
    Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken);

    /// <returns>The new last day of service, or <c>null</c> when the gym does not exist.</returns>
    Task<DateOnly?> ExtendAsync(Guid tenantId, int months, decimal amount, Guid actorId, CancellationToken cancellationToken);

    /// <returns><c>false</c> when the gym does not exist.</returns>
    Task<bool> SetStatusAsync(Guid tenantId, string status, Guid actorId, CancellationToken cancellationToken);

    Task<IReadOnlyList<MonthlyRevenue>> RevenueAsync(int months, CancellationToken cancellationToken);

    Task<TenantOwner?> FindOwnerAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>What the platform operator does with the gyms. Callers are platform administrators (enforced at the API).</summary>
public sealed class PlatformService(IPlatformStore store, PasswordSetupService welcome)
{
    public const int MaxMonths = 24;

    public const decimal MaxAmount = 100_000m;

    public Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken) => store.ListAsync(cancellationToken);

    public async Task<DateOnly> ExtendAsync(Guid tenantId, int? months, decimal? amount, Guid actorId, CancellationToken cancellationToken)
    {
        var errors = new ErrorBag();
        if (months is null or < 1 or > MaxMonths)
        {
            errors.Add("months", "invalid");
        }

        if (amount is null)
        {
            errors.Add("amount", "required");
        }
        else if (amount < 0 || amount > MaxAmount || decimal.Round(amount.Value, 2) != amount)
        {
            errors.Add("amount", "invalid");
        }

        errors.ThrowIfAny();
        return await store.ExtendAsync(tenantId, months!.Value, amount!.Value, actorId, cancellationToken)
            ?? throw new NotFoundException("tenant");
    }

    public async Task SetSuspendedAsync(Guid tenantId, bool suspended, Guid actorId, CancellationToken cancellationToken)
    {
        if (!await store.SetStatusAsync(tenantId, suspended ? "suspended" : "active", actorId, cancellationToken))
        {
            throw new NotFoundException("tenant");
        }
    }

    public Task<IReadOnlyList<MonthlyRevenue>> RevenueAsync(CancellationToken cancellationToken) => store.RevenueAsync(12, cancellationToken);

    /// <summary>Sends a fresh "create your password" link to a gym's owner who never set one.</summary>
    public async Task ResendWelcomeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var owner = await store.FindOwnerAsync(tenantId, cancellationToken) ?? throw new NotFoundException("tenant");
        if (!owner.MustChangePassword)
        {
            throw new ConflictException("owner_already_active");
        }

        if (owner.Email is null)
        {
            throw new ConflictException("owner_without_email");
        }

        await welcome.SendWelcomeAsync(
            new WelcomeRecipient(owner.UserId, owner.Email, owner.DisplayName, owner.Username, owner.GymName, owner.GymSlug),
            cancellationToken);
    }
}
