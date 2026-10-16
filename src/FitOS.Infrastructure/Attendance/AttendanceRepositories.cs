using System.ComponentModel.DataAnnotations.Schema;
using FitOS.Application.Attendance;
using FitOS.Application.Common;
using FitOS.Domain.Attendance;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitOS.Infrastructure.Attendance;

public sealed class AttendanceRepository(FitOsDbContext db) : IAttendanceRepository
{
    public Task<AttendanceEntry?> GetOpenAsync(Guid customerId, CancellationToken cancellationToken) =>
        db.Attendance.FirstOrDefaultAsync(
            a => a.CustomerId == customerId && a.CheckOut == null && a.Result == AttendanceResult.Granted,
            cancellationToken);

    public async Task<PagedResult<AttendanceView>> SearchAsync(AttendanceQuery query, CancellationToken cancellationToken)
    {
        var rows = from a in db.Attendance.AsNoTracking()
                   join c in db.Customers.AsNoTracking() on a.CustomerId equals c.Id
                   select new { Entry = a, c.Name, c.ClientCode };

        if (query.FromUtc is { } from)
        {
            rows = rows.Where(r => r.Entry.CheckIn >= from);
        }

        if (query.ToUtcExclusive is { } to)
        {
            rows = rows.Where(r => r.Entry.CheckIn < to);
        }

        if (query.CustomerId is { } customerId)
        {
            rows = rows.Where(r => r.Entry.CustomerId == customerId);
        }

        if (query.Result is { } result)
        {
            rows = rows.Where(r => r.Entry.Result == result);
        }

        var total = await rows.CountAsync(cancellationToken);
        var page = await rows
            .OrderByDescending(r => r.Entry.CheckIn)
            .ThenBy(r => r.Entry.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<AttendanceView>(
            page.Select(r => new AttendanceView(r.Entry, r.Name, r.ClientCode)).ToList(),
            query.Page,
            query.PageSize,
            total);
    }

    public void Add(AttendanceEntry entry) => db.Attendance.Add(entry);

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "attendance_one_open_uq" })
        {
            throw new ConflictException("already_inside");
        }
    }
}

public sealed class BranchRepository(FitOsDbContext db) : IBranchRepository
{
    public async Task<IReadOnlyList<Guid>> ActiveIdsAsync(CancellationToken cancellationToken) =>
        await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM branches WHERE is_active ORDER BY name, id")
            .ToListAsync(cancellationToken);
}

public sealed class BusinessHoursRepository(FitOsDbContext db) : IBusinessHoursRepository
{
    public async Task<BusinessDay?> GetAsync(Guid branchId, int isoWeekday, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<HoursRow>($"SELECT is_open, open_time, close_time FROM business_hours WHERE branch_id = {branchId} AND weekday = {isoWeekday}")
            .ToListAsync(cancellationToken);

        return rows is [var row] ? new BusinessDay(row.IsOpen, row.OpenTime, row.CloseTime) : null;
    }

    private sealed class HoursRow
    {
        [Column("is_open")]
        public bool IsOpen { get; set; }

        [Column("open_time")]
        public TimeOnly OpenTime { get; set; }

        [Column("close_time")]
        public TimeOnly CloseTime { get; set; }
    }
}
