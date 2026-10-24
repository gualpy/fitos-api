using FitOS.Application.Auth;
using FitOS.Application.Common;

namespace FitOS.Application.Team;

public sealed record AccessStatus(bool HasAccess, string? Email, bool IsActive, bool PasswordSet);

/// <summary>
/// Lets whoever enrols a customer give them a login for the app. Reception, administrators and owners can; the customer
/// receives an invitation e-mail (in the gym's name) with a one-time link, and logs in with that e-mail.
/// </summary>
public sealed class MemberAccessService(IUserStore store, TeamService team)
{
    public async Task<AccessStatus> StatusAsync(Guid customerId, CancellationToken cancellationToken)
    {
        _ = await store.FindCustomerAsync(customerId, cancellationToken) ?? throw new NotFoundException("customer");
        var user = await store.FindCustomerUserAsync(customerId, cancellationToken);
        return user is null ? new AccessStatus(false, null, false, false) : new AccessStatus(true, user.Email, user.IsActive, user.PasswordSet);
    }

    /// <param name="email">The address to invite; when omitted, the customer's own e-mail is used.</param>
    public async Task<InviteResult> GrantAsync(Guid customerId, string? email, CancellationToken cancellationToken)
    {
        var customer = await store.FindCustomerAsync(customerId, cancellationToken) ?? throw new NotFoundException("customer");
        if (!customer.IsActive)
        {
            throw new ConflictException("customer_inactive");
        }

        if (await store.FindCustomerUserAsync(customerId, cancellationToken) is not null)
        {
            throw new ConflictException("access_already_granted");
        }

        var address = string.IsNullOrWhiteSpace(email) ? (customer.Email ?? string.Empty) : email.Trim();
        var errors = new ErrorBag();
        TeamService.ValidateEmail(errors, address);
        errors.ThrowIfAny();

        var id = await team.CreateAccountAsync(customer.Name, address, "customer", customerId, cancellationToken);
        return new InviteResult(id, await team.TrySendAsync(id, customer.Name, address, WelcomeKind.Member, null, cancellationToken));
    }

    public async Task<bool> ResendAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await store.FindCustomerAsync(customerId, cancellationToken) ?? throw new NotFoundException("customer");
        var user = await store.FindCustomerUserAsync(customerId, cancellationToken) ?? throw new NotFoundException("access");
        if (user.PasswordSet)
        {
            throw new ConflictException("already_active");
        }

        if (user.Email is null)
        {
            throw new ConflictException("no_email");
        }

        return await team.TrySendAsync(user.UserId, customer.Name, user.Email, WelcomeKind.Member, null, cancellationToken);
    }
}
