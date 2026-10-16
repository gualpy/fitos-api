using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;

namespace FitOS.Api.Auth;

public static class AuthPolicies
{
    /// <summary>Any valid token, even one that still has to change its temporary password (/auth/me, /auth/change-password).</summary>
    public const string Authenticated = "authenticated";

    public const string PlatformAdmin = "platform_admin";

    /// <summary>Owner, admin or reception of a gym.</summary>
    public const string Staff = "staff";

    /// <summary>Owner or admin of a gym (prices, cancellations, deactivations).</summary>
    public const string Manager = "manager";

    /// <summary>Anyone who belongs to a gym, including its customers (never platform administrators).</summary>
    public const string TenantMember = "tenant_member";

    public static void Configure(AuthorizationOptions options)
    {
        // The default for every endpoint: a user who has not replaced the temporary password can do nothing else.
        options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(NoPendingPasswordChange)
            .Build();

        options.AddPolicy(Authenticated, policy => policy.RequireAuthenticatedUser());

        options.AddPolicy(Staff, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole("owner", "admin", "reception")
            .RequireAssertion(NoPendingPasswordChange));

        options.AddPolicy(Manager, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole("owner", "admin")
            .RequireAssertion(NoPendingPasswordChange));

        options.AddPolicy(TenantMember, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole("owner", "admin", "reception", "customer")
            .RequireAssertion(NoPendingPasswordChange));

        options.AddPolicy(PlatformAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole("platform_admin")
            .RequireAssertion(NoPendingPasswordChange));
    }

    private static bool NoPendingPasswordChange(AuthorizationHandlerContext context) =>
        !context.User.HasClaim(JwtAccessTokenIssuer.PasswordChangeClaim, "true");
}
