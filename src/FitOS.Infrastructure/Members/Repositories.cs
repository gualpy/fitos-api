using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Memberships;
using FitOS.Application.Plans;
using FitOS.Domain.Customers;
using FitOS.Domain.Memberships;
using FitOS.Domain.Plans;
using FitOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitOS.Infrastructure.Members;

internal static class SaveHelper
{
    /// <summary>Saves, translating the database's own rules into the application's conflict codes.</summary>
    public static async Task SaveAsync(FitOsDbContext db, IReadOnlyDictionary<string, string> constraintToCode, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { ConstraintName: { } constraint } pg
            && pg.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation
            && constraintToCode.TryGetValue(constraint, out var code))
        {
            throw new ConflictException(code);
        }
    }
}

public sealed class PlanRepository(FitOsDbContext db) : IPlanRepository
{
    private static readonly Dictionary<string, string> Constraints = new() { ["plans_name_uq"] = "plan_name_taken" };

    public async Task<IReadOnlyList<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
        await db.Plans.AsNoTracking()
            .Where(p => includeInactive || p.IsActive)
            .OrderBy(p => p.Name.ToLower())
            .ToListAsync(cancellationToken);

    public Task<Plan?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Plans.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public void Add(Plan plan) => db.Plans.Add(plan);

    public Task SaveAsync(CancellationToken cancellationToken) => SaveHelper.SaveAsync(db, Constraints, cancellationToken);
}

public sealed class CustomerRepository(FitOsDbContext db) : ICustomerRepository
{
    private static readonly Dictionary<string, string> Constraints = new()
    {
        ["customers_ident_uq"] = "identification_taken",
        ["customers_rfid_uq"] = "rfid_card_taken",
    };

    public async Task<PagedResult<CustomerListItem>> SearchAsync(CustomerSearch search, DateOnly today, CancellationToken cancellationToken)
    {
        var query = db.Customers.AsNoTracking();

        if (search.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (search.Text is { } text)
        {
            var like = $"%{EscapeLike(text)}%";
            long? code = long.TryParse(text, out var parsed) ? parsed : null;
            // The escape character must be passed explicitly: without it EF emits ESCAPE '' and "\%" would not work.
            query = query.Where(c =>
                EF.Functions.ILike(c.Name, like, "\\")
                || (c.Identification != null && EF.Functions.ILike(c.Identification, like, "\\"))
                || (c.Email != null && EF.Functions.ILike(c.Email, like, "\\"))
                || (code != null && c.ClientCode == code));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(c => c.Name.ToLower())
            .ThenBy(c => c.ClientCode)
            .Skip((search.Page - 1) * search.PageSize)
            .Take(search.PageSize)
            .Select(c => new
            {
                Customer = c,
                ActiveUntil = db.Memberships
                    .Where(m => m.CustomerId == c.Id && m.Status == MembershipStatus.Active && m.StartsOn <= today && m.EndsOn >= today)
                    .Max(m => (DateOnly?)m.EndsOn),
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<CustomerListItem>(
            rows.Select(r => new CustomerListItem(r.Customer, r.ActiveUntil)).ToList(),
            search.Page,
            search.PageSize,
            total);
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<Customer?> GetByClientCodeAsync(long clientCode, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.ClientCode == clientCode, cancellationToken);

    public Task<Customer?> GetByRfidCardAsync(string rfidCard, CancellationToken cancellationToken) =>
        db.Customers.FirstOrDefaultAsync(c => c.RfidCard == rfidCard, cancellationToken);

    public void Add(Customer customer) => db.Customers.Add(customer);

    public Task SaveAsync(CancellationToken cancellationToken) => SaveHelper.SaveAsync(db, Constraints, cancellationToken);

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

public sealed class MembershipRepository(FitOsDbContext db) : IMembershipRepository
{
    private static readonly Dictionary<string, string> Constraints = new() { ["memberships_no_overlap"] = "membership_overlap" };

    public async Task<IReadOnlyList<(Membership Membership, string PlanName)>> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var rows = await (
            from m in db.Memberships.AsNoTracking()
            where m.CustomerId == customerId
            join p in db.Plans.AsNoTracking() on m.PlanId equals p.Id
            orderby m.StartsOn descending
            select new { Membership = m, PlanName = p.Name })
            .ToListAsync(cancellationToken);

        return rows.Select(r => (r.Membership, r.PlanName)).ToList();
    }

    public Task<DateOnly?> LatestEndAsync(Guid customerId, CancellationToken cancellationToken) =>
        db.Memberships
            .Where(m => m.CustomerId == customerId && m.Status != MembershipStatus.Cancelled)
            .MaxAsync(m => (DateOnly?)m.EndsOn, cancellationToken);

    public Task<Membership?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Memberships.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

    public Task<bool> HasCurrentAsync(Guid customerId, DateOnly today, CancellationToken cancellationToken) =>
        db.Memberships.AnyAsync(
            m => m.CustomerId == customerId && m.Status == MembershipStatus.Active && m.StartsOn <= today && m.EndsOn >= today,
            cancellationToken);

    public void Add(Membership membership) => db.Memberships.Add(membership);

    public Task SaveAsync(CancellationToken cancellationToken) => SaveHelper.SaveAsync(db, Constraints, cancellationToken);
}
