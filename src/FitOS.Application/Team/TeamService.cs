using System.Text.RegularExpressions;
using FitOS.Application.Auth;
using FitOS.Application.Common;
using FitOS.Application.Email;
using FitOS.Application.Onboarding;
using FitOS.Domain.Tenancy;

namespace FitOS.Application.Team;

public sealed record Actor(Guid UserId, UserRole Role);

public sealed record InviteInput(string? Name, string? Email, string? Role);

/// <param name="EmailSent">False when the account was created but the mail server refused the invitation: resend it.</param>
public sealed record InviteResult(Guid Id, bool EmailSent);

/// <summary>
/// The people who work at a gym. The owner creates administrators and receptionists; an administrator only receptionists.
/// Nobody receives a password: an invitation e-mail carries a one-time link to create it.
/// </summary>
public sealed partial class TeamService(IUserStore store, IPasswordService passwords, PasswordSetupService setup)
{
    private static readonly string[] OwnerCanInvite = ["admin", "reception"];

    private static readonly string[] AdminCanInvite = ["reception"];

    public Task<IReadOnlyList<TeamMember>> ListAsync(CancellationToken cancellationToken) => store.ListStaffAsync(cancellationToken);

    public async Task<InviteResult> InviteAsync(Actor actor, InviteInput input, CancellationToken cancellationToken)
    {
        var name = (input.Name ?? string.Empty).Trim();
        var email = (input.Email ?? string.Empty).Trim();
        var role = input.Role ?? string.Empty;

        var errors = new ErrorBag();
        if (name.Length == 0)
        {
            errors.Add("name", "required");
        }
        else if (name.Length > 120)
        {
            errors.Add("name", "too_long");
        }

        ValidateEmail(errors, email);
        if (role is not ("admin" or "reception"))
        {
            errors.Add("role", "invalid");
        }

        errors.ThrowIfAny();

        if (!(actor.Role == UserRole.Owner ? OwnerCanInvite : actor.Role == UserRole.Admin ? AdminCanInvite : []).Contains(role))
        {
            throw new ForbiddenException("role_not_allowed");
        }

        var id = await CreateAccountAsync(name, email, role, null, cancellationToken);
        return new InviteResult(id, await TrySendAsync(id, name, email, cancellationToken));
    }

    public async Task SetActiveAsync(Actor actor, Guid id, bool active, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(actor, id, cancellationToken);
        if (!active)
        {
            if (id == actor.UserId)
            {
                throw new ConflictException("cannot_deactivate_self");
            }

            if (target.Role == "owner" && target.IsActive && await store.CountActiveOwnersAsync(cancellationToken) <= 1)
            {
                throw new ConflictException("last_owner");
            }
        }

        await store.SetActiveAsync(id, active, cancellationToken);
        if (!active)
        {
            await store.RevokeSessionsAsync(id, "deactivated", cancellationToken);
        }
    }

    public async Task ChangeRoleAsync(Actor actor, Guid id, string? role, CancellationToken cancellationToken)
    {
        if (actor.Role != UserRole.Owner)
        {
            throw new ForbiddenException("owner_only");
        }

        if (role is not ("admin" or "reception"))
        {
            throw new ValidationFailedException("role", "invalid");
        }

        var target = await ManageableAsync(actor, id, cancellationToken);
        if (target.Role == "owner")
        {
            throw new ConflictException("owner_role_fixed");
        }

        if (id == actor.UserId)
        {
            throw new ConflictException("cannot_change_own_role");
        }

        await store.SetRoleAsync(id, role, cancellationToken);
    }

    public async Task<bool> ResendInviteAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        var target = await ManageableAsync(actor, id, cancellationToken);
        if (target.PasswordSet)
        {
            throw new ConflictException("already_active");
        }

        if (target.Email is null)
        {
            throw new ConflictException("no_email");
        }

        return await TrySendAsync(id, target.DisplayName, target.Email, cancellationToken);
    }

    internal static void ValidateEmail(ErrorBag errors, string email)
    {
        if (email.Length == 0)
        {
            errors.Add("email", "required");
        }
        else if (email.Length > 254 || !EmailPattern().IsMatch(email))
        {
            errors.Add("email", "invalid_format");
        }
    }

    /// <summary>Creates a user with a password nobody knows; the invitation link sets the real one.</summary>
    internal async Task<Guid> CreateAccountAsync(string name, string email, string role, Guid? customerId, CancellationToken cancellationToken)
    {
        var username = await UsernameGenerator.FromEmailAsync(email, u => store.UsernameExistsAsync(u, cancellationToken));
        return await store.CreateAsync(new NewUser(username, passwords.Hash(TemporaryPassword.Generate(32)), name, email, role, customerId), cancellationToken);
    }

    /// <summary>The invitation goes out in the gym's name; if the mail server fails the account still exists.</summary>
    internal async Task<bool> TrySendAsync(Guid userId, string name, string email, CancellationToken cancellationToken)
    {
        var gym = await store.GymAsync(cancellationToken);
        try
        {
            // The person logs in with the e-mail they just received this at, so that is what the message shows.
            await setup.SendWelcomeAsync(new WelcomeRecipient(userId, email, name, email, gym.Name, gym.Slug), cancellationToken);
            return true;
        }
        catch (EmailDeliveryException)
        {
            return false;
        }
    }

    /// <summary>The target, or an error when the actor may not manage them: owners manage everyone, administrators only receptionists.</summary>
    private async Task<TeamMember> ManageableAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        if (actor.Role is not (UserRole.Owner or UserRole.Admin))
        {
            throw new ForbiddenException("manager_only");
        }

        var target = await store.FindStaffAsync(id, cancellationToken) ?? throw new NotFoundException("user");
        if (actor.Role == UserRole.Admin && target.Role != "reception" && id != actor.UserId)
        {
            throw new ForbiddenException("role_not_allowed");
        }

        return target;
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();
}
