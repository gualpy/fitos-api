using System.Security.Claims;
using FitOS.Api.Auth;
using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Memberships;
using FitOS.Application.Plans;
using FitOS.Domain.Customers;
using FitOS.Domain.Memberships;
using FitOS.Domain.Plans;

namespace FitOS.Api.Members;

/// <summary>Plans, customers (socios) and memberships of the caller's gym. The gym comes from the token, never from the URL.</summary>
public static class MembersEndpoints
{
    public static IEndpointRouteBuilder MapMembers(this IEndpointRouteBuilder app)
    {
        var plans = app.MapGroup("/plans");
        plans.MapGet("/", ListPlans).RequireAuthorization(AuthPolicies.TenantMember);
        plans.MapGet("/{id:guid}", GetPlan).RequireAuthorization(AuthPolicies.TenantMember);
        plans.MapPost("/", CreatePlan).RequireAuthorization(AuthPolicies.Manager);
        plans.MapPut("/{id:guid}", UpdatePlan).RequireAuthorization(AuthPolicies.Manager);
        plans.MapPut("/{id:guid}/active", SetPlanActive).RequireAuthorization(AuthPolicies.Manager);

        var customers = app.MapGroup("/customers");
        customers.MapGet("/", SearchCustomers).RequireAuthorization(AuthPolicies.Staff);
        customers.MapPost("/", CreateCustomer).RequireAuthorization(AuthPolicies.Staff);
        customers.MapGet("/{id:guid}", GetCustomer).RequireAuthorization(AuthPolicies.TenantMember);
        customers.MapPut("/{id:guid}", UpdateCustomer).RequireAuthorization(AuthPolicies.Staff);
        customers.MapPut("/{id:guid}/status", SetCustomerStatus).RequireAuthorization(AuthPolicies.Manager);
        customers.MapGet("/{id:guid}/memberships", ListMemberships).RequireAuthorization(AuthPolicies.TenantMember);
        customers.MapPost("/{id:guid}/memberships", AssignMembership).RequireAuthorization(AuthPolicies.Staff);

        app.MapPost("/memberships/{id:guid}/cancel", CancelMembership).RequireAuthorization(AuthPolicies.Manager);
        return app;
    }

    // ---- plans ----------------------------------------------------------------------------------------------

    private static async Task<IResult> ListPlans(bool? includeInactive, ClaimsPrincipal user, PlanService service, CancellationToken ct)
    {
        // Customers only ever see what they can buy.
        var all = includeInactive == true && !IsCustomer(user);
        return Results.Ok((await service.ListAsync(all, ct)).Select(ToResponse));
    }

    private static async Task<IResult> GetPlan(Guid id, ClaimsPrincipal user, PlanService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.GetAsync(id, includeInactive: !IsCustomer(user), ct)));

    private static async Task<IResult> CreatePlan(PlanRequest body, PlanService service, CancellationToken ct)
    {
        var plan = await service.CreateAsync(ToInput(body), ct);
        return Results.Created($"/plans/{plan.Id}", ToResponse(plan));
    }

    private static async Task<IResult> UpdatePlan(Guid id, PlanRequest body, PlanService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.UpdateAsync(id, ToInput(body), ct)));

    private static async Task<IResult> SetPlanActive(Guid id, ActiveRequest body, PlanService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.SetActiveAsync(id, body.IsActive, ct)));

    // ---- customers ------------------------------------------------------------------------------------------

    private static async Task<IResult> SearchCustomers(string? q, string? status, int? page, int? pageSize, CustomerService service, CancellationToken ct)
    {
        CustomerStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<CustomerStatus>(status, ignoreCase: true, out var value))
            {
                throw new ValidationFailedException("status", "invalid");
            }

            parsed = value;
        }

        var result = await service.SearchAsync(q, parsed, page ?? 1, pageSize ?? 25, ct);
        return Results.Ok(new
        {
            items = result.Items.Select(i => ToResponse(i.Customer, i.ActiveUntil)),
            result.Page,
            result.PageSize,
            result.Total,
        });
    }

    private static async Task<IResult> CreateCustomer(CustomerRequest body, CustomerService service, CancellationToken ct)
    {
        var customer = await service.CreateAsync(ToInput(body), ct);
        return Results.Created($"/customers/{customer.Id}", ToResponse(customer, null));
    }

    private static async Task<IResult> GetCustomer(Guid id, ClaimsPrincipal user, CustomerService customers, MembershipService memberships, CancellationToken ct)
    {
        EnsureCanSee(user, id);
        var customer = await customers.GetAsync(id, ct);
        var all = await memberships.ListForCustomerAsync(id, ct);

        // What matters at the desk: the membership covering today, else the next one coming.
        var relevant = all.FirstOrDefault(m => m.State == MembershipState.Current)
            ?? all.Where(m => m.State == MembershipState.Scheduled).OrderBy(m => m.Membership.StartsOn).FirstOrDefault();

        return Results.Ok(new
        {
            customer = ToResponse(customer, relevant?.State == MembershipState.Current ? relevant.Membership.EndsOn : null),
            membership = relevant is null ? null : ToResponse(relevant),
        });
    }

    private static async Task<IResult> UpdateCustomer(Guid id, CustomerRequest body, CustomerService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.UpdateAsync(id, ToInput(body), ct), null));

    private static async Task<IResult> SetCustomerStatus(Guid id, StatusRequest body, CustomerService service, CancellationToken ct)
    {
        if (!Enum.TryParse<CustomerStatus>(body.Status, ignoreCase: true, out var status))
        {
            throw new ValidationFailedException("status", "invalid");
        }

        return Results.Ok(ToResponse(await service.SetStatusAsync(id, status, ct), null));
    }

    // ---- memberships ----------------------------------------------------------------------------------------

    private static async Task<IResult> ListMemberships(Guid id, ClaimsPrincipal user, MembershipService service, CancellationToken ct)
    {
        EnsureCanSee(user, id);
        return Results.Ok((await service.ListForCustomerAsync(id, ct)).Select(ToResponse));
    }

    private static async Task<IResult> AssignMembership(Guid id, AssignRequest body, MembershipService service, CancellationToken ct)
    {
        if (body.PlanId is not { } planId)
        {
            throw new ValidationFailedException("planId", "required");
        }

        var view = await service.AssignAsync(id, planId, body.StartsOn, ct);
        return Results.Created($"/customers/{id}/memberships", ToResponse(view));
    }

    private static async Task<IResult> CancelMembership(Guid id, MembershipService service, CancellationToken ct) =>
        Results.Ok(ToResponse(await service.CancelAsync(id, ct)));

    // ---- helpers --------------------------------------------------------------------------------------------

    private static bool IsCustomer(ClaimsPrincipal user) => CustomerAccess.IsCustomer(user);

    private static void EnsureCanSee(ClaimsPrincipal user, Guid customerId) => CustomerAccess.EnsureCanSee(user, customerId);

    private static PlanInput ToInput(PlanRequest r) => new(r.Name, r.Description, r.Price, r.IvaRate, r.DurationMonths, r.IsPromotion);

    private static CustomerInput ToInput(CustomerRequest r) =>
        new(r.Name, r.IdentificationType, r.Identification, r.Email, r.Phone, r.Gender, r.BirthDate, r.RfidCard);

    private static object ToResponse(Plan p) => new
    {
        p.Id, p.Name, p.Description, p.Price, p.IvaRate, p.DurationMonths, p.IsPromotion, p.IsActive,
    };

    private static object ToResponse(Customer c, DateOnly? activeUntil) => new
    {
        c.Id,
        c.ClientCode,
        c.Name,
        c.IdentificationType,
        c.Identification,
        c.Email,
        c.Phone,
        c.Gender,
        c.BirthDate,
        c.RfidCard,
        Status = c.Status.ToString().ToLowerInvariant(),
        c.JoinedAt,
        ActiveUntil = activeUntil,
    };

    private static object ToResponse(MembershipView v) => new
    {
        v.Membership.Id,
        v.Membership.CustomerId,
        v.Membership.PlanId,
        v.PlanName,
        v.Membership.StartsOn,
        v.Membership.EndsOn,
        State = v.State.ToString().ToLowerInvariant(),
    };

    public sealed record PlanRequest(string? Name, string? Description, decimal? Price, decimal? IvaRate, int? DurationMonths, bool? IsPromotion);

    public sealed record ActiveRequest(bool IsActive);

    public sealed record CustomerRequest(
        string? Name,
        string? IdentificationType,
        string? Identification,
        string? Email,
        string? Phone,
        string? Gender,
        DateOnly? BirthDate,
        string? RfidCard);

    public sealed record StatusRequest(string? Status);

    public sealed record AssignRequest(Guid? PlanId, DateOnly? StartsOn);
}
