using FitOS.Application.Common;
using FitOS.Application.Memberships;
using FitOS.Application.Sales;
using FitOS.Domain.Customers;
using FitOS.Domain.Memberships;
using FitOS.Domain.Plans;
using FitOS.Domain.Products;
using FitOS.UnitTests.Members;

namespace FitOS.UnitTests.Sales;

public sealed class MembershipSaleServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    private sealed class RecordingSales : ISaleRepository
    {
        public SaleDraft? Written { get; private set; }

        public Exception? FailWith { get; set; }

        public Task<IReadOnlyList<Product>> FindProductsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Product>>([]);

        public Task CreateAsync(SaleDraft sale, CancellationToken cancellationToken)
        {
            if (FailWith is not null)
            {
                throw FailWith;
            }

            Written = sale;
            return Task.CompletedTask;
        }
    }

    private sealed class Codes : IPaymentCodes
    {
        public string? CodeFor(string method) => method == "cash" ? "01" : null;
    }

    private readonly MemoryCustomers _customers = new();

    private readonly MemoryPlans _plans = new();

    private readonly MemoryMemberships _memberships = new();

    private readonly RecordingSales _sales = new();

    private readonly Customer _customer = new() { Name = "Ana", TenantId = Guid.NewGuid() };

    private readonly Plan _plan = new() { Name = "Mensual", DurationMonths = 1, Price = 40m, IvaRate = 15m };

    public MembershipSaleServiceTests()
    {
        _customers.Items.Add(_customer);
        _plans.Items.Add(_plan);
    }

    private MembershipSaleService Service()
    {
        var tenant = new FixedTenant(_customer.TenantId);
        var membershipService = new MembershipService(_memberships, _customers, _plans, tenant, new FixedClock(Today));
        return new MembershipSaleService(membershipService, _sales, new Codes(), tenant, TimeProvider.System);
    }

    private static List<PaymentInput> Pay(decimal amount, string method = "cash") => [new PaymentInput(method, amount)];

    [Fact]
    public async Task The_sale_carries_one_plan_line_priced_from_the_plan_with_iva()
    {
        var result = await Service().AssignAsync(_customer.Id, _plan.Id, null, Pay(46m), Guid.NewGuid(), CancellationToken.None);

        var draft = _sales.Written!;
        var line = Assert.Single(draft.Lines);
        Assert.Equal(_plan.Id, line.PlanId);
        Assert.Null(line.ProductId);
        Assert.Equal(40m, line.UnitPrice);
        Assert.Equal(15m, line.IvaRate);
        Assert.Equal((40m, 6m, 46m), (draft.Totals.Subtotal, draft.Totals.TaxTotal, draft.Totals.Total));
        Assert.Equal(_customer.Id, draft.CustomerId);
        Assert.Equal("01", Assert.Single(draft.Payments).SriCode);
        Assert.Equal(draft.Id, result.Sale.Id);
    }

    [Fact]
    public async Task The_membership_is_written_with_the_sale_and_linked_to_it()
    {
        var result = await Service().AssignAsync(_customer.Id, _plan.Id, null, Pay(46m), null, CancellationToken.None);

        var membership = _sales.Written!.Membership!;
        Assert.Equal((Today, new DateOnly(2026, 11, 15)), (membership.StartsOn, membership.EndsOn));
        Assert.Equal(_sales.Written.Id, result.Membership.Membership.SaleId);
        Assert.Equal(MembershipState.Current, result.Membership.State);
        Assert.Empty(_memberships.Items); // nothing is saved on the side: the sale repository owns the transaction
    }

    [Fact]
    public async Task A_second_paid_plan_queues_after_the_current_one()
    {
        _memberships.Items.Add(new Membership { TenantId = _customer.TenantId, CustomerId = _customer.Id, PlanId = _plan.Id, StartsOn = Today, EndsOn = new DateOnly(2026, 11, 15) });

        var result = await Service().AssignAsync(_customer.Id, _plan.Id, null, Pay(46m), null, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 16), result.Membership.Membership.StartsOn);
        Assert.Equal(MembershipState.Scheduled, result.Membership.State);
    }

    [Theory]
    [InlineData(45.99)]
    [InlineData(46.01)]
    public async Task Paying_anything_but_the_exact_total_is_refused_and_nothing_is_written(double amount)
    {
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Service().AssignAsync(_customer.Id, _plan.Id, null, Pay((decimal)amount), null, CancellationToken.None));

        Assert.Equal("payment_mismatch", ex.Code);
        Assert.Null(_sales.Written);
    }

    [Fact]
    public async Task Payments_are_required_and_validated()
    {
        var none = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Service().AssignAsync(_customer.Id, _plan.Id, null, null, null, CancellationToken.None));
        var bad = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Service().AssignAsync(_customer.Id, _plan.Id, null, Pay(46m, "bitcoin"), null, CancellationToken.None));

        Assert.Equal("required", none.Errors["payments"]);
        Assert.Equal("invalid", bad.Errors["payments"]);
        Assert.Null(_sales.Written);
    }

    [Fact]
    public async Task The_same_rules_as_a_free_assignment_apply()
    {
        _plan.IsActive = false;
        var inactivePlan = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Service().AssignAsync(_customer.Id, _plan.Id, null, Pay(46m), null, CancellationToken.None));
        _plan.IsActive = true;
        _customer.Status = CustomerStatus.Inactive;
        var inactiveCustomer = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Service().AssignAsync(_customer.Id, _plan.Id, null, Pay(46m), null, CancellationToken.None));
        _customer.Status = CustomerStatus.Active;
        var past = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            Service().AssignAsync(_customer.Id, _plan.Id, Today.AddDays(-1), Pay(46m), null, CancellationToken.None));

        Assert.Equal("inactive", inactivePlan.Errors["planId"]);
        Assert.Equal("inactive", inactiveCustomer.Errors["customerId"]);
        Assert.Equal("in_the_past", past.Errors["startsOn"]);
        Assert.Null(_sales.Written);
    }

    [Fact]
    public async Task Unknown_customer_or_plan_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Service().AssignAsync(Guid.NewGuid(), _plan.Id, null, Pay(46m), null, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Service().AssignAsync(_customer.Id, Guid.NewGuid(), null, Pay(46m), null, CancellationToken.None));
    }

    [Fact]
    public async Task An_overlap_found_while_writing_surfaces_as_a_conflict()
    {
        _sales.FailWith = new ConflictException("membership_overlap");

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            Service().AssignAsync(_customer.Id, _plan.Id, null, Pay(46m), null, CancellationToken.None));

        Assert.Equal("membership_overlap", ex.Code);
    }
}
