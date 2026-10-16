namespace FitOS.Domain.Attendance;

public enum AttendanceResult
{
    Granted,
    Denied,
}

/// <summary>One scan at the door: a granted entry (with an optional exit) or a denied attempt with its reason.</summary>
public sealed class AttendanceEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public Guid CustomerId { get; set; }

    public Guid? DeviceId { get; set; }

    public DateTimeOffset CheckIn { get; set; }

    public DateTimeOffset? CheckOut { get; set; }

    public AttendanceResult Result { get; set; } = AttendanceResult.Granted;

    /// <summary>customer_inactive, closed, outside_hours or no_active_membership; only when denied.</summary>
    public string? DenyReason { get; set; }

    /// <summary>Set when the system closed a forgotten entry (CheckOut then equals CheckIn: the real exit is unknown).</summary>
    public DateTimeOffset? AutoClosedAt { get; set; }
}
