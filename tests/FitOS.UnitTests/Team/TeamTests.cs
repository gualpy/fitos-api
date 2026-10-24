using FitOS.Application.Auth;
using FitOS.Application.Common;
using FitOS.Application.Team;
using FitOS.Domain.Tenancy;

namespace FitOS.UnitTests.Team;

public sealed class TeamTests
{
    private sealed class FakePasswords : IPasswordService
    {
        public string Hash(string password) => "h:" + password.Length;

        public bool Verify(string hash, string password) => true;

        public PasswordCheck Check(string hash, string password) => PasswordCheck.Success;

        public void VerifyDummy(string password)
        {
        }
    }

    private static readonly Actor Owner = new(Guid.NewGuid(), UserRole.Owner);

    private static readonly Actor Admin = new(Guid.NewGuid(), UserRole.Admin);

    private static readonly Actor Reception = new(Guid.NewGuid(), UserRole.Reception);

    private static (TeamService Team, MemberAccessService Access, FakeUserStore Store, FakeEmail Email) Build(bool failMail = false)
    {
        var store = new FakeUserStore();
        var email = new FakeEmail { Fail = failMail };
        var setup = new PasswordSetupService(
            new FakeSetupStore(), new FakePasswords(), email, new FakeLog(),
            new SetupLinkPolicy("https://app.test", TimeSpan.FromHours(48), TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(60)), new FixedTime(DateTimeOffset.UtcNow));
        var team = new TeamService(store, new FakePasswords(), setup);
        return (team, new MemberAccessService(store, team), store, email);
    }

    private static TeamMember Member(string role, bool active = true, bool passwordSet = true, string? email = "m@example.test") =>
        new(Guid.NewGuid(), "Nombre", email, role, active, passwordSet, null);

    // ---- inviting staff ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_invites_a_receptionist_who_gets_a_mail_in_the_gyms_name_and_no_password()
    {
        var (team, _, store, email) = Build();

        var result = await team.InviteAsync(Owner, new InviteInput(" Carla Pérez ", " Carla@Example.test ", "reception"), CancellationToken.None);

        Assert.True(result.EmailSent);
        var created = Assert.Single(store.Created);
        Assert.Equal("reception", created.Role);
        Assert.Equal("Carla Pérez", created.DisplayName);
        Assert.Equal("Carla@Example.test", created.Email);
        Assert.Equal("carla", created.Username); // internal: nobody has to know it
        Assert.Null(created.CustomerId);
        Assert.Equal("h:32", created.PasswordHash); // a random value nobody sees
        var message = Assert.Single(email.Sent);
        Assert.Equal("Gimnasio Titan", message.FromName);
        Assert.Equal("Gimnasio Titan te invitó a su equipo", message.Subject);
        Assert.Contains("recepción", message.Html);
        Assert.Contains("#setup=", message.Body);
    }

    [Theory]
    [InlineData("owner", "admin", true)]
    [InlineData("owner", "reception", true)]
    [InlineData("owner", "owner", false)]
    [InlineData("admin", "reception", true)]
    [InlineData("admin", "admin", false)]
    [InlineData("reception", "reception", false)]
    public async Task Who_may_create_which_role(string actorRole, string newRole, bool allowed)
    {
        var (team, _, store, _) = Build();
        var actor = new Actor(Guid.NewGuid(), UserRoleWire.Parse(actorRole));
        var input = new InviteInput("Alguien", "alguien@example.test", newRole);

        if (allowed)
        {
            await team.InviteAsync(actor, input, CancellationToken.None);
            Assert.Single(store.Created);
        }
        else if (newRole == "owner")
        {
            await Assert.ThrowsAsync<ValidationFailedException>(() => team.InviteAsync(actor, input, CancellationToken.None)); // there is no such invitable role
        }
        else
        {
            await Assert.ThrowsAsync<ForbiddenException>(() => team.InviteAsync(actor, input, CancellationToken.None));
            Assert.Empty(store.Created);
        }
    }

    [Theory]
    [InlineData("", "a@example.test", "reception", "name")]
    [InlineData("Ana", "", "reception", "email")]
    [InlineData("Ana", "sin-arroba", "reception", "email")]
    [InlineData("Ana", "a b@example.test", "reception", "email")]
    [InlineData("Ana", "a@example.test", "dios", "role")]
    public async Task Invalid_invitations_are_rejected_and_nothing_is_created(string name, string email, string role, string field)
    {
        var (team, _, store, _) = Build();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => team.InviteAsync(Owner, new InviteInput(name, email, role), CancellationToken.None));

        Assert.Contains(field, ex.Errors.Keys);
        Assert.Empty(store.Created);
    }

    [Fact]
    public async Task A_repeated_email_is_a_conflict_not_a_second_account()
    {
        var (team, _, store, _) = Build();
        await team.InviteAsync(Owner, new InviteInput("Ana", "ana@example.test", "reception"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => team.InviteAsync(Owner, new InviteInput("Otra Ana", "ANA@example.test", "reception"), CancellationToken.None));

        Assert.Equal("email_taken", ex.Code);
        Assert.Single(store.Created);
    }

    [Fact]
    public async Task A_failing_mail_server_still_creates_the_account()
    {
        var (team, _, store, _) = Build(failMail: true);

        var result = await team.InviteAsync(Owner, new InviteInput("Ana", "ana@example.test", "reception"), CancellationToken.None);

        Assert.False(result.EmailSent);
        Assert.Single(store.Created);
    }

    [Fact]
    public async Task Two_people_with_the_same_start_of_email_get_distinct_internal_usernames()
    {
        var (team, _, store, _) = Build();

        await team.InviteAsync(Owner, new InviteInput("Juan A", "juan@a.test", "reception"), CancellationToken.None);
        await team.InviteAsync(Owner, new InviteInput("Juan B", "juan@b.test", "reception"), CancellationToken.None);

        Assert.Equal(["juan", "juan2"], store.Created.Select(u => u.Username).ToArray());
    }

    // ---- managing staff ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Deactivating_closes_the_sessions_and_nobody_can_deactivate_themselves()
    {
        var (team, _, store, _) = Build();
        var carla = Member("reception");
        store.Staff.Add(carla);

        await team.SetActiveAsync(Owner, carla.Id, false, CancellationToken.None);

        Assert.Equal(["active:False", "revoke:deactivated"], store.Calls);

        var me = Member("admin");
        store.Staff.Add(me);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => team.SetActiveAsync(new Actor(me.Id, UserRole.Admin), me.Id, false, CancellationToken.None));
        Assert.Equal("cannot_deactivate_self", ex.Code);
    }

    [Fact]
    public async Task The_last_active_owner_cannot_be_deactivated()
    {
        var (team, _, store, _) = Build();
        var owner = Member("owner");
        store.Staff.Add(owner);
        store.ActiveOwners = 1;

        var ex = await Assert.ThrowsAsync<ConflictException>(() => team.SetActiveAsync(Owner, owner.Id, false, CancellationToken.None));

        Assert.Equal("last_owner", ex.Code);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task An_administrator_manages_receptionists_but_not_administrators_or_owners()
    {
        var (team, _, store, _) = Build();
        var recep = Member("reception");
        var otherAdmin = Member("admin");
        var owner = Member("owner");
        store.Staff.AddRange([recep, otherAdmin, owner]);

        await team.SetActiveAsync(Admin, recep.Id, false, CancellationToken.None);
        await Assert.ThrowsAsync<ForbiddenException>(() => team.SetActiveAsync(Admin, otherAdmin.Id, false, CancellationToken.None));
        await Assert.ThrowsAsync<ForbiddenException>(() => team.SetActiveAsync(Admin, owner.Id, false, CancellationToken.None));
    }

    [Fact]
    public async Task Receptionists_manage_nobody_and_unknown_users_are_not_found()
    {
        var (team, _, store, _) = Build();
        var recep = Member("reception");
        store.Staff.Add(recep);

        await Assert.ThrowsAsync<ForbiddenException>(() => team.SetActiveAsync(Reception, recep.Id, false, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => team.SetActiveAsync(Owner, Guid.NewGuid(), false, CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_owner_changes_roles_and_never_the_owners_own()
    {
        var (team, _, store, _) = Build();
        var recep = Member("reception");
        var owner = Member("owner");
        store.Staff.AddRange([recep, owner]);

        await team.ChangeRoleAsync(Owner, recep.Id, "admin", CancellationToken.None);
        Assert.Equal(["role:admin"], store.Calls);
        await Assert.ThrowsAsync<ForbiddenException>(() => team.ChangeRoleAsync(Admin, recep.Id, "admin", CancellationToken.None));
        await Assert.ThrowsAsync<ValidationFailedException>(() => team.ChangeRoleAsync(Owner, recep.Id, "owner", CancellationToken.None));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => team.ChangeRoleAsync(Owner, owner.Id, "admin", CancellationToken.None));
        Assert.Equal("owner_role_fixed", ex.Code);
    }

    [Fact]
    public async Task Resending_an_invitation_only_works_for_someone_who_has_not_set_a_password()
    {
        var (team, _, store, email) = Build();
        var pending = Member("reception", passwordSet: false);
        var active = Member("reception");
        var noMail = Member("reception", passwordSet: false, email: null);
        store.Staff.AddRange([pending, active, noMail]);

        Assert.True(await team.ResendInviteAsync(Owner, pending.Id, CancellationToken.None));
        Assert.Single(email.Sent);
        Assert.Equal("already_active", (await Assert.ThrowsAsync<ConflictException>(() => team.ResendInviteAsync(Owner, active.Id, CancellationToken.None))).Code);
        Assert.Equal("no_email", (await Assert.ThrowsAsync<ConflictException>(() => team.ResendInviteAsync(Owner, noMail.Id, CancellationToken.None))).Code);
    }

    // ---- giving a customer access -----------------------------------------------------------------------------

    private static CustomerForAccess Customer(string? email = "ana@example.test", bool active = true) => new(Guid.NewGuid(), "Ana Socia", email, active);

    [Fact]
    public async Task Access_uses_the_customers_email_by_default_and_the_invitation_names_the_gym()
    {
        var (_, access, store, email) = Build();
        store.Customer = Customer();

        var result = await access.GrantAsync(store.Customer.Id, null, CancellationToken.None);

        Assert.True(result.EmailSent);
        var created = Assert.Single(store.Created);
        Assert.Equal("customer", created.Role);
        Assert.Equal(store.Customer.Id, created.CustomerId);
        Assert.Equal("ana@example.test", created.Email);
        Assert.Equal("Gimnasio Titan", Assert.Single(email.Sent).FromName);
    }

    [Fact]
    public async Task A_given_email_wins_over_the_customers_and_one_is_required_when_there_is_none()
    {
        var (_, access, store, _) = Build();
        store.Customer = Customer(email: null);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => access.GrantAsync(store.Customer.Id, null, CancellationToken.None));
        Assert.Equal("required", ex.Errors["email"]);

        await access.GrantAsync(store.Customer.Id, " otra@example.test ", CancellationToken.None);
        Assert.Equal("otra@example.test", Assert.Single(store.Created).Email);
    }

    [Fact]
    public async Task Access_is_refused_for_unknown_inactive_or_already_enabled_customers()
    {
        var (_, access, store, _) = Build();

        await Assert.ThrowsAsync<NotFoundException>(() => access.GrantAsync(Guid.NewGuid(), "a@example.test", CancellationToken.None));

        store.Customer = Customer(active: false);
        Assert.Equal("customer_inactive", (await Assert.ThrowsAsync<ConflictException>(() => access.GrantAsync(store.Customer.Id, null, CancellationToken.None))).Code);

        store.Customer = Customer();
        store.CustomerAccount = new CustomerUser(Guid.NewGuid(), "ana@example.test", true, false);
        Assert.Equal("access_already_granted", (await Assert.ThrowsAsync<ConflictException>(() => access.GrantAsync(store.Customer.Id, null, CancellationToken.None))).Code);
        Assert.Empty(store.Created);
    }

    [Fact]
    public async Task The_status_and_the_resend_follow_the_customers_account()
    {
        var (_, access, store, email) = Build();
        store.Customer = Customer();

        Assert.False((await access.StatusAsync(store.Customer.Id, CancellationToken.None)).HasAccess);
        await Assert.ThrowsAsync<NotFoundException>(() => access.ResendAsync(store.Customer.Id, CancellationToken.None));

        store.CustomerAccount = new CustomerUser(Guid.NewGuid(), "ana@example.test", true, PasswordSet: false);
        Assert.True((await access.StatusAsync(store.Customer.Id, CancellationToken.None)).HasAccess);
        Assert.True(await access.ResendAsync(store.Customer.Id, CancellationToken.None));
        Assert.Single(email.Sent);

        store.CustomerAccount = store.CustomerAccount with { PasswordSet = true };
        Assert.Equal("already_active", (await Assert.ThrowsAsync<ConflictException>(() => access.ResendAsync(store.Customer.Id, CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData("juan.perez@x.test", "juan.perez")]
    [InlineData("JUAN@x.test", "juan")]
    [InlineData("a@x.test", "auser")]
    [InlineData("__@x.test", "user")]
    [InlineData("ñandú+tag@x.test", "andtag")]
    public async Task Usernames_are_derived_from_the_email_and_always_valid(string email, string expected)
    {
        var username = await UsernameGenerator.FromEmailAsync(email, _ => Task.FromResult(false));

        Assert.Equal(expected, username);
        Assert.Matches("^[a-z0-9][a-z0-9._-]{2,49}$", username);
    }
}
