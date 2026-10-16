namespace FitOS.Domain.Memberships;

/// <summary>Persisted status. "Expired" is derived from the dates (see <see cref="MembershipRules.StateOf"/>), not relied upon.</summary>
public enum MembershipStatus
{
    Active,
    Expired,
    Cancelled,
}

/// <summary>What a membership means today.</summary>
public enum MembershipState
{
    Scheduled,
    Current,
    Expired,
    Cancelled,
}

public sealed class Membership
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    public Guid PlanId { get; set; }

    public DateOnly StartsOn { get; set; }

    /// <summary>Last day covered (inclusive).</summary>
    public DateOnly EndsOn { get; set; }

    public MembershipStatus Status { get; set; } = MembershipStatus.Active;

    /// <summary>The sale that paid for it; null for memberships assigned by hand.</summary>
    public Guid? SaleId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class MembershipRules
{
    /// <summary>
    /// Dates for a new membership. It starts on the requested day (default: today) unless the customer is already
    /// covered until later, in which case it queues right after the last covered day. Duration counts calendar
    /// months and the end is inclusive: a 1-month plan starting Jan 15 ends Feb 14.
    /// </summary>
    public static (DateOnly Start, DateOnly End) Schedule(DateOnly today, DateOnly? requestedStart, DateOnly? latestEnd, int durationMonths)
    {
        var start = requestedStart ?? today;
        if (latestEnd is { } lastCovered && lastCovered >= start)
        {
            start = lastCovered.AddDays(1);
        }

        return (start, start.AddMonths(durationMonths).AddDays(-1));
    }

    public static MembershipState StateOf(MembershipStatus status, DateOnly startsOn, DateOnly endsOn, DateOnly today)
    {
        if (status == MembershipStatus.Cancelled)
        {
            return MembershipState.Cancelled;
        }

        if (status == MembershipStatus.Expired || endsOn < today)
        {
            return MembershipState.Expired;
        }

        return startsOn > today ? MembershipState.Scheduled : MembershipState.Current;
    }
}
