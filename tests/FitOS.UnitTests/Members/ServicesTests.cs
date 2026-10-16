using FitOS.Application.Abstractions;
using FitOS.Application.Common;
using FitOS.Application.Customers;
using FitOS.Application.Memberships;
using FitOS.Application.Plans;
using FitOS.Domain.Customers;
using FitOS.Domain.Memberships;
using FitOS.Domain.Plans;

namespace FitOS.UnitTests.Members;

internal sealed class FixedTenant(Guid? id) : ITenantContext
{
    public Guid? TenantId { get; } = id;
}

internal sealed class FixedClock(DateOnly today) : ITenantClock
{
    public Task<TimeZoneInfo> ZoneAsync(CancellationToken cancellationToken) => Task.FromResult(TimeZoneInfo.Utc);

    public Task<DateOnly> TodayAsync(CancellationToken cancellationToken) => Task.FromResult(today);
}

internal sealed class MemoryPlans : IPlanRepository
{
    public List<Plan> Items { get; } = [];

    public int Saves { get; private set; }

    public Task<IReadOnlyList<Plan>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Plan>>(Items.Where(p => includeInactive || p.IsActive).ToList());

    public Task<Plan?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.FirstOrDefault(p => p.Id == id));

    public void Add(Plan plan) => Items.Add(plan);

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryCustomers : ICustomerRepository
{
    public List<Customer> Items { get; } = [];

    public Task<PagedResult<CustomerListItem>> SearchAsync(CustomerSearch search, DateOnly today, CancellationToken cancellationToken) =>
        throw new NotSupportedException("covered by the integration tests");

    public Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.FirstOrDefault(c => c.Id == id));

    public Task<Customer?> GetByClientCodeAsync(long clientCode, CancellationToken cancellationToken) => Task.FromResult(Items.FirstOrDefault(c => c.ClientCode == clientCode));

    public Task<Customer?> GetByRfidCardAsync(string rfidCard, CancellationToken cancellationToken) => Task.FromResult(Items.FirstOrDefault(c => c.RfidCard == rfidCard));

    public void Add(Customer customer) => Items.Add(customer);

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class MemoryMemberships : IMembershipRepository
{
    public List<Membership> Items { get; } = [];

    public Dictionary<Guid, string> PlanNames { get; } = [];

    public Task<IReadOnlyList<(Membership Membership, string PlanName)>> ListForCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Membership, string)>>(
            Items.Where(m => m.CustomerId == customerId).OrderByDescending(m => m.StartsOn).Select(m => (m, PlanNames.GetValueOrDefault(m.PlanId, "?"))).ToList());

    public Task<DateOnly?> LatestEndAsync(Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Where(m => m.CustomerId == customerId && m.Status != MembershipStatus.Cancelled).Select(m => (DateOnly?)m.EndsOn).Max());

    public Task<Membership?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.FirstOrDefault(m => m.Id == id));

    public Task<bool> HasCurrentAsync(Guid customerId, DateOnly today, CancellationToken cancellationToken) =>
        Task.FromResult(Items.Any(m => m.CustomerId == customerId && m.Status == MembershipStatus.Active && m.StartsOn <= today && m.EndsOn >= today));

    public void Add(Membership membership) => Items.Add(membership);

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class PlanServiceTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static (PlanService Service, MemoryPlans Repo) Create()
    {
        var repo = new MemoryPlans();
        return (new PlanService(repo, new FixedTenant(Tenant)), repo);
    }

    private static PlanInput Valid(Func<PlanInput, PlanInput>? change = null)
    {
        var input = new PlanInput("  Mensual  ", "  Acceso total ", 40m, 15m, 1, null);
        return change?.Invoke(input) ?? input;
    }

    [Fact]
    public async Task Create_normalises_and_stamps_the_tenant()
    {
        var (service, repo) = Create();

        var plan = await service.CreateAsync(Valid(), CancellationToken.None);

        Assert.Equal(Tenant, plan.TenantId);
        Assert.Equal("Mensual", plan.Name);
        Assert.Equal("Acceso total", plan.Description);
        Assert.True(plan.IsActive);
        Assert.False(plan.IsPromotion);
        Assert.Single(repo.Items);
        Assert.Equal(1, repo.Saves);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("name", "x")]
    [InlineData("price", "-1")]
    [InlineData("price", "100000.01")]
    [InlineData("price", "10.005")]
    [InlineData("iva", "12")]
    [InlineData("months", "0")]
    [InlineData("months", "61")]
    public async Task Invalid_plans_are_rejected_before_saving(string field, string value)
    {
        var (service, repo) = Create();
        var input = Valid(i => field switch
        {
            "name" => i with { Name = value == "x" ? new string('x', 101) : value },
            "price" => i with { Price = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            "iva" => i with { IvaRate = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
            _ => i with { DurationMonths = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) },
        });

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.CreateAsync(input, CancellationToken.None));

        Assert.Empty(repo.Items);
        Assert.Equal(0, repo.Saves);
    }

    [Fact]
    public async Task Missing_price_and_duration_are_reported_together()
    {
        var (service, _) = Create();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => service.CreateAsync(new PlanInput("Plan", null, null, null, null, null), CancellationToken.None));

        Assert.Equal("required", ex.Errors["price"]);
        Assert.Equal("required", ex.Errors["durationMonths"]);
    }

    [Fact]
    public async Task Iva_defaults_to_15_and_free_plans_are_allowed()
    {
        var (service, _) = Create();

        var plan = await service.CreateAsync(Valid(i => i with { IvaRate = null, Price = 0m }), CancellationToken.None);

        Assert.Equal(15m, plan.IvaRate);
        Assert.Equal(0m, plan.Price);
    }

    [Fact]
    public async Task Update_keeps_the_promotion_flag_when_not_provided()
    {
        var (service, _) = Create();
        var plan = await service.CreateAsync(Valid(i => i with { IsPromotion = true }), CancellationToken.None);

        var updated = await service.UpdateAsync(plan.Id, Valid(i => i with { Price = 50m, IsPromotion = null }), CancellationToken.None);

        Assert.True(updated.IsPromotion);
        Assert.Equal(50m, updated.Price);
    }

    [Fact]
    public async Task Missing_or_inactive_plans_are_not_found_for_customers()
    {
        var (service, _) = Create();
        var plan = await service.CreateAsync(Valid(), CancellationToken.None);
        await service.SetActiveAsync(plan.Id, false, CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(Guid.NewGuid(), true, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAsync(plan.Id, includeInactive: false, CancellationToken.None));
        Assert.False((await service.GetAsync(plan.Id, includeInactive: true, CancellationToken.None)).IsActive);
    }

    [Fact]
    public async Task Without_a_tenant_in_scope_nothing_is_created()
    {
        var service = new PlanService(new MemoryPlans(), new FixedTenant(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Valid(), CancellationToken.None));
    }
}

public sealed class CustomerServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    private static CustomerService Create(MemoryCustomers? repo = null) =>
        new(repo ?? new MemoryCustomers(), new FixedTenant(Guid.NewGuid()), new FixedClock(Today));

    private static CustomerInput Valid(Func<CustomerInput, CustomerInput>? change = null)
    {
        var input = new CustomerInput(" Ana Pérez ", "05", "1710034065", " ana@example.com ", "+593 99 123 4567", "f", new DateOnly(1990, 5, 1), " card-1 ");
        return change?.Invoke(input) ?? input;
    }

    [Fact]
    public async Task Create_trims_normalises_and_stamps_tenant_and_join_date()
    {
        var customer = await Create().CreateAsync(Valid(), CancellationToken.None);

        Assert.Equal("Ana Pérez", customer.Name);
        Assert.Equal("ana@example.com", customer.Email);
        Assert.Equal("F", customer.Gender);
        Assert.Equal("card-1", customer.RfidCard);
        Assert.Equal(Today, customer.JoinedAt);
        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.NotEqual(Guid.Empty, customer.TenantId);
    }

    [Fact]
    public async Task Everything_but_the_name_is_optional()
    {
        var customer = await Create().CreateAsync(new CustomerInput("Solo Nombre", null, null, null, "  ", "", null, null), CancellationToken.None);

        Assert.Null(customer.Identification);
        Assert.Null(customer.Phone);
        Assert.Null(customer.Gender);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("idtype-only", "05")]
    [InlineData("id-only", "1710034065")]
    [InlineData("cedula", "1710034066")]
    [InlineData("ruc", "1790016918001")]
    [InlineData("passport", "A1")]
    [InlineData("type", "99")]
    [InlineData("email", "not-an-email")]
    [InlineData("email", "Ana <a@b.com>")]
    [InlineData("phone", "abc")]
    [InlineData("phone", "123")]
    [InlineData("gender", "X")]
    [InlineData("birth", "future")]
    [InlineData("birth", "ancient")]
    [InlineData("rfid", "long")]
    public async Task Invalid_customers_are_rejected(string case_, string value)
    {
        var input = Valid(i => case_ switch
        {
            "name" => i with { Name = value },
            "idtype-only" => i with { Identification = null },
            "id-only" => i with { IdentificationType = null },
            "cedula" => i with { Identification = value },
            "ruc" => i with { IdentificationType = "04", Identification = value },
            "passport" => i with { IdentificationType = "06", Identification = value },
            "type" => i with { IdentificationType = value },
            "email" => i with { Email = value },
            "phone" => i with { Phone = value },
            "gender" => i with { Gender = value },
            "birth" => i with { BirthDate = value == "future" ? Today.AddDays(1) : Today.AddYears(-121) },
            _ => i with { RfidCard = new string('x', 51) },
        });

        await Assert.ThrowsAsync<ValidationFailedException>(() => Create().CreateAsync(input, CancellationToken.None));
    }

    [Theory]
    [InlineData("04", "1790016919001")]
    [InlineData("05", "1710034065")]
    [InlineData("06", "AB123456")]
    [InlineData("08", "XYZ-1"  )]
    public async Task Valid_identification_documents_by_type(string type, string id)
    {
        if (id.Contains('-', StringComparison.Ordinal))
        {
            // Foreign IDs are letters and digits only.
            await Assert.ThrowsAsync<ValidationFailedException>(() => Create().CreateAsync(Valid(i => i with { IdentificationType = type, Identification = id }), CancellationToken.None));
            return;
        }

        var customer = await Create().CreateAsync(Valid(i => i with { IdentificationType = type, Identification = id }), CancellationToken.None);

        Assert.Equal(id, customer.Identification);
    }

    [Fact]
    public async Task Page_size_is_capped_and_page_is_at_least_one()
    {
        var repo = new CapturingSearch();
        var service = new CustomerService(repo, new FixedTenant(Guid.NewGuid()), new FixedClock(Today));

        await service.SearchAsync("  ana ", null, -5, 10_000, CancellationToken.None);

        Assert.Equal(1, repo.Last!.Page);
        Assert.Equal(CustomerService.MaxPageSize, repo.Last.PageSize);
        Assert.Equal("ana", repo.Last.Text);
    }

    private sealed class CapturingSearch : ICustomerRepository
    {
        public CustomerSearch? Last { get; private set; }

        public Task<PagedResult<CustomerListItem>> SearchAsync(CustomerSearch search, DateOnly today, CancellationToken cancellationToken)
        {
            Last = search;
            return Task.FromResult(new PagedResult<CustomerListItem>([], search.Page, search.PageSize, 0));
        }

        public Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Customer?>(null);

        public Task<Customer?> GetByClientCodeAsync(long clientCode, CancellationToken cancellationToken) => Task.FromResult<Customer?>(null);

        public Task<Customer?> GetByRfidCardAsync(string rfidCard, CancellationToken cancellationToken) => Task.FromResult<Customer?>(null);

        public void Add(Customer customer)
        {
        }

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class MembershipServiceTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    private readonly MemoryCustomers _customers = new();

    private readonly MemoryPlans _plans = new();

    private readonly MemoryMemberships _memberships = new();

    private readonly Customer _customer = new() { Name = "Ana", TenantId = Guid.NewGuid() };

    private readonly Plan _plan = new() { Name = "Mensual", DurationMonths = 1, Price = 40 };

    public MembershipServiceTests()
    {
        _customers.Items.Add(_customer);
        _plans.Items.Add(_plan);
        _memberships.PlanNames[_plan.Id] = _plan.Name;
    }

    private MembershipService Service() =>
        new(_memberships, _customers, _plans, new FixedTenant(_customer.TenantId), new FixedClock(Today));

    [Fact]
    public async Task First_membership_starts_today_and_is_current()
    {
        var view = await Service().AssignAsync(_customer.Id, _plan.Id, null, CancellationToken.None);

        Assert.Equal(Today, view.Membership.StartsOn);
        Assert.Equal(new DateOnly(2026, 11, 15), view.Membership.EndsOn);
        Assert.Equal(MembershipState.Current, view.State);
        Assert.Equal("Mensual", view.PlanName);
        Assert.Equal(_customer.TenantId, view.Membership.TenantId);
    }

    [Fact]
    public async Task Renewing_while_covered_queues_the_next_membership_as_scheduled()
    {
        var service = Service();
        await service.AssignAsync(_customer.Id, _plan.Id, null, CancellationToken.None);

        var second = await service.AssignAsync(_customer.Id, _plan.Id, null, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 11, 16), second.Membership.StartsOn);
        Assert.Equal(MembershipState.Scheduled, second.State);
    }

    [Fact]
    public async Task A_cancelled_membership_frees_its_days()
    {
        var service = Service();
        var first = await service.AssignAsync(_customer.Id, _plan.Id, null, CancellationToken.None);
        await service.CancelAsync(first.Membership.Id, CancellationToken.None);

        var again = await service.AssignAsync(_customer.Id, _plan.Id, null, CancellationToken.None);

        Assert.Equal(Today, again.Membership.StartsOn);
    }

    [Fact]
    public async Task Inactive_customer_inactive_plan_and_past_start_are_rejected_together()
    {
        _customer.Status = CustomerStatus.Inactive;
        _plan.IsActive = false;

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(
            () => Service().AssignAsync(_customer.Id, _plan.Id, Today.AddDays(-1), CancellationToken.None));

        Assert.Equal(["customerId", "planId", "startsOn"], ex.Errors.Keys.Order().ToArray());
        Assert.Empty(_memberships.Items);
    }

    [Fact]
    public async Task Unknown_customer_or_plan_is_not_found()
    {
        var service = Service();

        await Assert.ThrowsAsync<NotFoundException>(() => service.AssignAsync(Guid.NewGuid(), _plan.Id, null, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.AssignAsync(_customer.Id, Guid.NewGuid(), null, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ListForCustomerAsync(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => service.CancelAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancelling_twice_is_harmless_and_the_list_shows_states_newest_first()
    {
        var service = Service();
        var first = await service.AssignAsync(_customer.Id, _plan.Id, null, CancellationToken.None);
        await service.AssignAsync(_customer.Id, _plan.Id, null, CancellationToken.None);
        await service.CancelAsync(first.Membership.Id, CancellationToken.None);
        var again = await service.CancelAsync(first.Membership.Id, CancellationToken.None);

        var list = await service.ListForCustomerAsync(_customer.Id, CancellationToken.None);

        Assert.Equal(MembershipState.Cancelled, again.State);
        Assert.Equal([MembershipState.Scheduled, MembershipState.Cancelled], list.Select(v => v.State).ToArray());
    }
}
