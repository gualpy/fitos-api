using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Memberships;
using FitOS.Domain.Attendance;
using FitOS.Domain.Customers;

namespace FitOS.Application.Attendance;

public sealed record IdentifyCustomer(Guid? CustomerId, long? ClientCode, string? RfidCard);

public sealed record CheckInInput(IdentifyCustomer Who, Guid? BranchId);

public sealed record AttendanceFilter(DateOnly? From, DateOnly? To, Guid? CustomerId, AttendanceResult? Result, int Page, int PageSize);

/// <summary>A scan as shown to the user: date, times (in the gym's own zone) and stay length are already worked out.</summary>
public sealed record AttendanceItem(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    long ClientCode,
    Guid BranchId,
    DateTimeOffset CheckIn,
    DateTimeOffset? CheckOut,
    DateOnly Date,
    TimeOnly CheckInTime,
    TimeOnly? CheckOutTime,
    int? Minutes,
    string Result,
    string? DenyReason,
    bool AutoClosed);

/// <summary>One scan joined with the customer's name, as the repository returns it.</summary>
public sealed record AttendanceView(AttendanceEntry Entry, string CustomerName, long ClientCode);

public sealed record AttendanceQuery(DateTimeOffset? FromUtc, DateTimeOffset? ToUtcExclusive, Guid? CustomerId, AttendanceResult? Result, int Page, int PageSize);

public interface IAttendanceRepository
{
    /// <summary>The customer's granted entry that has no exit yet (tracked, so it can be closed).</summary>
    Task<AttendanceEntry?> GetOpenAsync(Guid customerId, CancellationToken cancellationToken);

    Task<PagedResult<AttendanceView>> SearchAsync(AttendanceQuery query, CancellationToken cancellationToken);

    void Add(AttendanceEntry entry);

    /// <exception cref="ConflictException"><c>already_inside</c> (a second open entry was refused by the database)</exception>
    Task SaveAsync(CancellationToken cancellationToken);
}

public interface IBranchRepository
{
    Task<IReadOnlyList<Guid>> ActiveIdsAsync(CancellationToken cancellationToken);
}

public interface IBusinessHoursRepository
{
    /// <summary>Hours of that ISO weekday (Mon = 1) at the branch; <c>null</c> when none were configured.</summary>
    Task<BusinessDay?> GetAsync(Guid branchId, int isoWeekday, CancellationToken cancellationToken);
}

public sealed class AttendanceService(
    IAttendanceRepository attendance,
    ICustomerRepository customers,
    IMembershipRepository memberships,
    IBranchRepository branches,
    IBusinessHoursRepository hours,
    ITenantSettings settings,
    ITenantClock clock,
    ITenantContext tenant,
    TimeProvider time)
{
    public const int MaxPageSize = 100;

    /// <summary>An entry still open after this long was forgotten, not a real session.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// Registers a scan. Both outcomes are recorded: a granted entry, or a denied attempt with its reason
    /// (customer_inactive, closed, outside_hours, no_active_membership).
    /// </summary>
    public async Task<AttendanceItem> CheckInAsync(CheckInInput input, CancellationToken cancellationToken)
    {
        var customer = await FindCustomerAsync(input.Who, cancellationToken);
        var branchId = await ResolveBranchAsync(input.BranchId, cancellationToken);
        var now = time.GetUtcNow();

        var open = await attendance.GetOpenAsync(customer.Id, cancellationToken);
        if (open is not null)
        {
            if (now - open.CheckIn < StaleAfter)
            {
                throw new ConflictException("already_inside");
            }

            // Forgotten exit: close it explicitly (and visibly) so it does not block the new entry.
            open.CheckOut = open.CheckIn;
            open.AutoClosedAt = now;
            await attendance.SaveAsync(cancellationToken);
        }

        var zone = await clock.ZoneAsync(cancellationToken);
        var today = TenantTime.LocalDate(zone, now);
        var decision = AccessPolicy.Decide(
            customer.Status == CustomerStatus.Active,
            await memberships.HasCurrentAsync(customer.Id, today, cancellationToken),
            await settings.AllowWithoutPlanAsync(cancellationToken),
            await hours.GetAsync(branchId, TenantTime.IsoWeekday(today), cancellationToken),
            TenantTime.LocalTime(zone, now));

        var entry = new AttendanceEntry
        {
            TenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope."),
            BranchId = branchId,
            CustomerId = customer.Id,
            CheckIn = now,
            Result = decision.Granted ? AttendanceResult.Granted : AttendanceResult.Denied,
            DenyReason = decision.Reason,
        };
        attendance.Add(entry);
        await attendance.SaveAsync(cancellationToken);

        return ToItem(entry, customer.Name, customer.ClientCode, zone);
    }

    public async Task<AttendanceItem> CheckOutAsync(IdentifyCustomer who, CancellationToken cancellationToken)
    {
        var customer = await FindCustomerAsync(who, cancellationToken);
        var entry = await attendance.GetOpenAsync(customer.Id, cancellationToken) ?? throw new ConflictException("no_open_entry");

        entry.CheckOut = time.GetUtcNow();
        await attendance.SaveAsync(cancellationToken);

        return ToItem(entry, customer.Name, customer.ClientCode, await clock.ZoneAsync(cancellationToken));
    }

    public async Task<PagedResult<AttendanceItem>> ListAsync(AttendanceFilter filter, CancellationToken cancellationToken)
    {
        var zone = await clock.ZoneAsync(cancellationToken);
        var errors = new ErrorBag();
        if (filter.From is { } from && filter.To is { } to && to < from)
        {
            errors.Add("to", "before_from");
        }

        errors.ThrowIfAny();

        var query = new AttendanceQuery(
            filter.From is { } f ? TenantTime.StartOfDayUtc(zone, f) : null,
            filter.To is { } t ? TenantTime.StartOfDayUtc(zone, t.AddDays(1)) : null, // the whole last day is included
            filter.CustomerId,
            filter.Result,
            Math.Max(filter.Page, 1),
            Math.Clamp(filter.PageSize, 1, MaxPageSize));

        var page = await attendance.SearchAsync(query, cancellationToken);
        return new PagedResult<AttendanceItem>(
            page.Items.Select(v => ToItem(v.Entry, v.CustomerName, v.ClientCode, zone)).ToList(),
            page.Page,
            page.PageSize,
            page.Total);
    }

    public async Task<PagedResult<AttendanceItem>> ListForCustomerAsync(Guid customerId, AttendanceFilter filter, CancellationToken cancellationToken)
    {
        _ = await customers.GetAsync(customerId, cancellationToken) ?? throw new NotFoundException("customer");
        return await ListAsync(filter with { CustomerId = customerId }, cancellationToken);
    }

    private async Task<Customer> FindCustomerAsync(IdentifyCustomer who, CancellationToken cancellationToken)
    {
        var rfid = string.IsNullOrWhiteSpace(who.RfidCard) ? null : who.RfidCard.Trim();
        var given = (who.CustomerId is null ? 0 : 1) + (who.ClientCode is null ? 0 : 1) + (rfid is null ? 0 : 1);
        if (given != 1)
        {
            throw new ValidationFailedException("identifier", "exactly_one_required");
        }

        var customer = who.CustomerId is { } id ? await customers.GetAsync(id, cancellationToken)
            : who.ClientCode is { } code ? await customers.GetByClientCodeAsync(code, cancellationToken)
            : await customers.GetByRfidCardAsync(rfid!, cancellationToken);

        return customer ?? throw new NotFoundException("customer");
    }

    private async Task<Guid> ResolveBranchAsync(Guid? requested, CancellationToken cancellationToken)
    {
        var active = await branches.ActiveIdsAsync(cancellationToken);
        if (requested is { } id)
        {
            return active.Contains(id) ? id : throw new NotFoundException("branch");
        }

        return active.Count switch
        {
            1 => active[0],
            0 => throw new ValidationFailedException("branchId", "no_active_branch"),
            _ => throw new ValidationFailedException("branchId", "required"),
        };
    }

    private static AttendanceItem ToItem(AttendanceEntry e, string customerName, long clientCode, TimeZoneInfo zone) => new(
        e.Id,
        e.CustomerId,
        customerName,
        clientCode,
        e.BranchId,
        e.CheckIn,
        e.CheckOut,
        TenantTime.LocalDate(zone, e.CheckIn),
        TenantTime.LocalTime(zone, e.CheckIn),
        e.CheckOut is { } exit ? TenantTime.LocalTime(zone, exit) : null,
        e.CheckOut is { } end ? (int)Math.Round((end - e.CheckIn).TotalMinutes) : null,
        e.Result.ToString().ToLowerInvariant(),
        e.DenyReason,
        e.AutoClosedAt is not null);
}
