using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FitOS.Infrastructure.Auth;

public sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider time) : IAccessTokenIssuer
{
    public const string TenantClaim = "tenant_id";

    public const string RoleClaim = "role";

    public const string CustomerClaim = "customer_id";

    /// <summary>Present ("true") while the user must change a temporary password; blocks everything but /auth/*.</summary>
    public const string PasswordChangeClaim = "pwd_change";

    private readonly JwtSecurityTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessToken Issue(LoginRecord user)
    {
        var jwt = options.Value;
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(jwt.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(RoleClaim, user.Role.ToWire()),
        };

        // Platform admins have no tenant: the claim is absent, so tenant tables return no rows.
        if (user.TenantId is { } tenantId)
        {
            claims.Add(new Claim(TenantClaim, tenantId.ToString()));
        }

        if (user.MustChangePassword)
        {
            claims.Add(new Claim(PasswordChangeClaim, "true"));
        }

        if (user.CustomerId is { } customerId)
        {
            claims.Add(new Claim(CustomerClaim, customerId.ToString()));
        }

        var token = new JwtSecurityToken(
            jwt.Issuer,
            jwt.Audience,
            claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                SecurityAlgorithms.HmacSha256));

        return new AccessToken(_handler.WriteToken(token), expires);
    }
}
