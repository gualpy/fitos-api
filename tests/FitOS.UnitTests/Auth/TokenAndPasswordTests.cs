using System.IdentityModel.Tokens.Jwt;
using System.Text;
using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;
using FitOS.Infrastructure.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FitOS.UnitTests.Auth;

public sealed class TokenAndPasswordTests
{
    private const string Key = "unit-test-signing-key-0123456789-abcdef";

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static JwtAccessTokenIssuer Issuer(int minutes = 30) => new(
        Options.Create(new JwtOptions { Issuer = "fitos", Audience = "fitos-api", SigningKey = Key, AccessTokenMinutes = minutes }),
        new FixedTime(Now));

    private static LoginRecord Record(Guid? tenantId, UserRole role = UserRole.Admin, Guid? customerId = null) =>
        new(Guid.NewGuid(), tenantId, "secret-hash", role, "Ana", customerId, true, false, "active");

    private static JwtSecurityToken Read(AccessToken token) => new JwtSecurityTokenHandler().ReadJwtToken(token.Value);

    [Fact]
    public void Token_carries_tenant_role_and_name_and_expires_when_configured()
    {
        var tenant = Guid.NewGuid();
        var issued = Issuer(minutes: 30).Issue(Record(tenant));

        var jwt = Read(issued);

        Assert.Equal(tenant.ToString(), jwt.Claims.Single(c => c.Type == "tenant_id").Value);
        Assert.Equal("admin", jwt.Claims.Single(c => c.Type == "role").Value);
        Assert.Equal("Ana", jwt.Claims.Single(c => c.Type == "name").Value);
        Assert.Equal(Now.AddMinutes(30), issued.ExpiresAt);
        Assert.Equal(issued.ExpiresAt.UtcDateTime, jwt.ValidTo);
        Assert.Equal("fitos", jwt.Issuer);
        Assert.Contains("fitos-api", jwt.Audiences);
    }

    [Fact]
    public void Platform_admin_token_has_no_tenant_claim()
    {
        var jwt = Read(Issuer().Issue(Record(tenantId: null, UserRole.PlatformAdmin)));

        Assert.DoesNotContain(jwt.Claims, c => c.Type == "tenant_id");
        Assert.Equal("platform_admin", jwt.Claims.Single(c => c.Type == "role").Value);
    }

    [Fact]
    public void Customer_token_carries_the_customer_id()
    {
        var customer = Guid.NewGuid();

        var jwt = Read(Issuer().Issue(Record(Guid.NewGuid(), UserRole.Customer, customer)));

        Assert.Equal(customer.ToString(), jwt.Claims.Single(c => c.Type == "customer_id").Value);
    }

    [Fact]
    public void Token_never_contains_the_password_hash()
    {
        var issued = Issuer().Issue(Record(Guid.NewGuid()));

        Assert.DoesNotContain("secret-hash", issued.Value);
        Assert.DoesNotContain("secret-hash", Read(issued).Payload.SerializeToJson());
    }

    [Fact]
    public void Token_is_signed_with_the_configured_key_only()
    {
        var issued = Issuer().Issue(Record(Guid.NewGuid()));
        var handler = new JwtSecurityTokenHandler();
        TokenValidationParameters Parameters(string key) => new()
        {
            ValidIssuer = "fitos",
            ValidAudience = "fitos-api",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = false,
        };

        handler.ValidateToken(issued.Value, Parameters(Key), out _);
        Assert.ThrowsAny<SecurityTokenException>(() =>
            handler.ValidateToken(issued.Value, Parameters("another-key-another-key-another-key-00"), out _));
    }

    [Fact]
    public void Password_hash_is_salted_verifiable_and_not_the_password()
    {
        var service = new IdentityPasswordService();

        var first = service.Hash("Correct-Horse-9");
        var second = service.Hash("Correct-Horse-9");

        Assert.NotEqual(first, second); // per-hash salt
        Assert.DoesNotContain("Correct-Horse-9", first);
        Assert.True(service.Verify(first, "Correct-Horse-9"));
        Assert.False(service.Verify(first, "correct-horse-9"));
        Assert.False(service.Verify(first, string.Empty));
    }
}
