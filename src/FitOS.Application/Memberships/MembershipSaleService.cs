using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Application.Sales;
using FitOS.Domain.Memberships;
using FitOS.Domain.Sales;

namespace FitOS.Application.Memberships;

/// <summary>What the desk gets back after charging a plan.</summary>
public sealed record MembershipSale(MembershipView Membership, Sale Sale);

/// <summary>
/// Charges a plan: the sale (one plan line), its payments and the membership are written together,
/// so money without a membership, or a membership without money, cannot happen.
/// </summary>
public sealed class MembershipSaleService(
    MembershipService memberships,
    ISaleRepository sales,
    IPaymentCodes paymentCodes,
    ITenantContext tenant,
    TimeProvider time)
{
    public async Task<MembershipSale> AssignAsync(
        Guid customerId,
        Guid planId,
        DateOnly? startsOn,
        IReadOnlyList<PaymentInput>? paymentInputs,
        Guid? cashierId,
        CancellationToken cancellationToken)
    {
        var errors = new ErrorBag();
        var payments = PaymentInputs.Parse(paymentInputs, errors);
        errors.ThrowIfAny();

        var planned = await memberships.PlanAsync(customerId, planId, startsOn, cancellationToken);

        // The price and IVA come from the plan, never from the request.
        var plan = planned.Plan;
        var line = new PricedLine(null, plan.Id, plan.Name, 1, plan.Price, plan.IvaRate, SaleCalculator.LineTotal(plan.Price, 1));
        var totals = SaleCalculator.Totals([line]);
        var resolved = PaymentInputs.Resolve(payments, totals.Total, paymentCodes);

        var membership = new MembershipDraft(Guid.NewGuid(), customerId, plan.Id, planned.Start, planned.End);
        var draft = new SaleDraft(
            Guid.NewGuid(),
            tenant.TenantId ?? throw new InvalidOperationException("No tenant in scope."),
            customerId,
            cashierId,
            time.GetUtcNow(),
            totals,
            [line],
            resolved,
            membership);

        await sales.CreateAsync(draft, cancellationToken);

        var view = new MembershipView(
            new Membership
            {
                Id = membership.Id,
                TenantId = draft.TenantId,
                CustomerId = customerId,
                PlanId = plan.Id,
                StartsOn = planned.Start,
                EndsOn = planned.End,
                SaleId = draft.Id,
            },
            plan.Name,
            MembershipRules.StateOf(MembershipStatus.Active, planned.Start, planned.End, planned.Today));

        return new MembershipSale(view, new Sale(draft.Id, customerId, draft.CreatedAt, totals, [line], resolved));
    }
}
