using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Plans;
using FitOS.Domain.Customers;
using FitOS.Domain.Memberships;

namespace FitOS.Application.Memberships;

/// <summary>A membership together with the name of its plan and what it means today.</summary>
public sealed record MembershipView(Membership Membership, string PlanName, MembershipState State);

public interface IMembershipRepository
{
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<(Membership Membership, string PlanName)>> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Last covered day among the customer's non-cancelled memberships; null if none.</summary>
    Task<DateOnly?> LatestEndAsync(Guid customerId, CancellationToken cancellationToken);

    Task<Membership?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Does a non-cancelled membership cover <paramref name="today"/>?</summary>
    Task<bool> HasCurrentAsync(Guid customerId, DateOnly today, CancellationToken cancellationToken);

    void Add(Membership membership);

    /// <exception cref="ConflictException"><c>membership_overlap</c> (two requests raced for the same days)</exception>
    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed class MembershipService(
    IMembershipRepository memberships,
    ICustomerRepository customers,
    IPlanRepository plans,
    ITenantContext tenant,
    ITenantClock clock)
{
    public async Task<IReadOnlyList<MembershipView>> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        _ = await customers.GetAsync(customerId, cancellationToken) ?? throw new NotFoundException("customer");
        var today = await clock.TodayAsync(cancellationToken);

        return (await memberships.ListForCustomerAsync(customerId, cancellationToken))
            .Select(x => new MembershipView(x.Membership, x.PlanName, MembershipRules.StateOf(x.Membership.Status, x.Membership.StartsOn, x.Membership.EndsOn, today)))
            .ToList();
    }

    /// <summary>Assigns a plan by hand (courtesy, cash at the desk before the POS exists). Paid sales will link via <c>SaleId</c>.</summary>
    public async Task<MembershipView> AssignAsync(Guid customerId, Guid planId, DateOnly? startsOn, CancellationToken cancellationToken)
    {
        var customer = await customers.GetAsync(customerId, cancellationToken) ?? throw new NotFoundException("customer");
        var plan = await plans.GetAsync(planId, cancellationToken) ?? throw new NotFoundException("plan");

        var today = await clock.TodayAsync(cancellationToken);
        var errors = new ErrorBag();
        if (customer.Status != CustomerStatus.Active)
        {
            errors.Add("customerId", "inactive");
        }

        if (!plan.IsActive)
        {
            errors.Add("planId", "inactive");
        }

        if (startsOn is { } requested && requested < today)
        {
            errors.Add("startsOn", "in_the_past");
        }

        errors.ThrowIfAny();

        var latestEnd = await memberships.LatestEndAsync(customerId, cancellationToken);
        var (start, end) = MembershipRules.Schedule(today, startsOn, latestEnd, plan.DurationMonths);

        var membership = new Membership
        {
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope."),
            CustomerId = customerId,
            PlanId = planId,
            StartsOn = start,
            EndsOn = end,
        };
        memberships.Add(membership);
        await memberships.SaveAsync(cancellationToken);

        return new MembershipView(membership, plan.Name, MembershipRules.StateOf(membership.Status, start, end, today));
    }

    public async Task<MembershipView> CancelAsync(Guid membershipId, CancellationToken cancellationToken)
    {
        var membership = await memberships.GetAsync(membershipId, cancellationToken) ?? throw new NotFoundException("membership");
        var plan = await plans.GetAsync(membership.PlanId, cancellationToken) ?? throw new NotFoundException("plan");

        membership.Status = MembershipStatus.Cancelled; // idempotent
        await memberships.SaveAsync(cancellationToken);

        return new MembershipView(membership, plan.Name, MembershipState.Cancelled);
    }
}
