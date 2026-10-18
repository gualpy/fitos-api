using FitOS.Application.Dashboard;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitOS.Infrastructure.Dashboard;

/// <summary>
/// Read-only aggregates computed in SQL on the request's tenant-scoped connection: row-level security
/// keeps every count inside the caller's gym, so no query repeats the tenant filter by hand.
/// </summary>
public sealed class DashboardRepository(FitOsDbContext db) : IDashboardRepository
{
    public Task<int> CountCustomersAsync(CancellationToken cancellationToken) =>
        ScalarAsync(db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM customers"""), cancellationToken);

    public Task<int> CountActiveMembersAsync(DateOnly today, CancellationToken cancellationToken) =>
        ScalarAsync(
            db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM customers c
                WHERE c.status = 'active'
                  AND EXISTS (SELECT 1 FROM memberships m
                              WHERE m.customer_id = c.id AND m.status = 'active' AND m.starts_on <= {today} AND m.ends_on >= {today})
                """),
            cancellationToken);

    public Task<int> CountExpiringAsync(DateOnly today, int days, CancellationToken cancellationToken)
    {
        var last = today.AddDays(days);
        return ScalarAsync(
            db.Database.SqlQuery<int>($"""
                SELECT count(DISTINCT c.id)::int AS "Value"
                FROM customers c
                JOIN memberships m ON m.customer_id = c.id AND m.status = 'active'
                WHERE c.status = 'active'
                  AND m.starts_on <= {today} AND m.ends_on >= {today} AND m.ends_on <= {last}
                  AND NOT EXISTS (SELECT 1 FROM memberships n
                                  WHERE n.customer_id = c.id AND n.status = 'active' AND n.starts_on > m.ends_on)
                """),
            cancellationToken);
    }

    public Task<int> CountInsideAsync(CancellationToken cancellationToken) =>
        ScalarAsync(
            db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM attendance WHERE result = 'granted' AND check_out IS NULL"""),
            cancellationToken);

    public async Task<IReadOnlyList<GenderCount>> GenderBreakdownAsync(CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<GenderRow>($"""SELECT gender AS "Gender", count(*)::int AS "Count" FROM customers WHERE status = 'active' GROUP BY gender ORDER BY gender""")
            .Select(r => new GenderCount(r.Gender, r.Count))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DailyCount>> AttendancePerDayAsync(DashboardWindow window, CancellationToken cancellationToken)
    {
        var days = window.Days.ToArray();
        var starts = window.StartsUtc.ToArray();
        var ends = window.EndsUtc.ToArray();
        return await db.Database
            .SqlQuery<DayCountRow>($"""
                SELECT d.day AS "Day",
                       (SELECT count(DISTINCT a.customer_id)::int FROM attendance a
                        WHERE a.result = 'granted' AND a.check_in >= d.s AND a.check_in < d.e) AS "Count"
                FROM unnest({days}, {starts}, {ends}) AS d(day, s, e)
                ORDER BY d.day
                """)
            .Select(r => new DailyCount(r.Day, r.Count))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DailyAmount>> SalesPerDayAsync(DashboardWindow window, CancellationToken cancellationToken)
    {
        var days = window.Days.ToArray();
        var starts = window.StartsUtc.ToArray();
        var ends = window.EndsUtc.ToArray();
        return await db.Database
            .SqlQuery<DayAmountRow>($"""
                SELECT d.day AS "Day",
                       (SELECT coalesce(sum(s2.total), 0) FROM sales s2
                        WHERE s2.status = 'paid' AND s2.created_at >= d.s AND s2.created_at < d.e) AS "Amount"
                FROM unnest({days}, {starts}, {ends}) AS d(day, s, e)
                ORDER BY d.day
                """)
            .Select(r => new DailyAmount(r.Day, r.Amount))
            .ToListAsync(cancellationToken);
    }

    private static async Task<int> ScalarAsync(IQueryable<int> query, CancellationToken cancellationToken) =>
        (await query.ToListAsync(cancellationToken)).Single();

    // Shapes of the raw rows (property names are the aliased column names).
    private sealed record GenderRow(string? Gender, int Count);

    private sealed record DayCountRow(DateOnly Day, int Count);

    private sealed record DayAmountRow(DateOnly Day, decimal Amount);
}
