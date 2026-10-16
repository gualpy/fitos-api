using FitOS.Application.Auth;
using FitOS.Infrastructure.Auth;

namespace FitOS.UnitTests.Auth;

public sealed class PasswordHashingTests
{
    /// <summary>The iteration count is stored in the hash itself (ASP.NET Core Identity format v3, big-endian at bytes 5-8).</summary>
    private static int IterationsOf(string hash)
    {
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(0x01, bytes[0]);
        return (bytes[5] << 24) | (bytes[6] << 16) | (bytes[7] << 8) | bytes[8];
    }

    [Fact]
    public void New_hashes_use_the_configured_iteration_count_and_the_default_meets_the_owasp_floor()
    {
        Assert.Equal(20_000, IterationsOf(new IdentityPasswordService(20_000).Hash("Correct-Horse-9!")));
        Assert.True(IdentityPasswordService.DefaultIterations >= 210_000);
        Assert.Equal(IdentityPasswordService.DefaultIterations, IterationsOf(new IdentityPasswordService().Hash("Correct-Horse-9!")));
    }

    [Fact]
    public void An_old_hash_still_verifies_but_is_flagged_for_an_upgrade()
    {
        var old = new IdentityPasswordService(1_000).Hash("Correct-Horse-9!");
        var current = new IdentityPasswordService(20_000);

        Assert.Equal(PasswordCheck.SuccessRehashNeeded, current.Check(old, "Correct-Horse-9!"));
        Assert.True(current.Verify(old, "Correct-Horse-9!")); // raising the cost never locks anyone out
        Assert.Equal(PasswordCheck.Failed, current.Check(old, "wrong"));
    }

    [Fact]
    public void A_hash_at_the_current_cost_or_stronger_needs_nothing()
    {
        var current = new IdentityPasswordService(20_000);

        Assert.Equal(PasswordCheck.Success, current.Check(current.Hash("Correct-Horse-9!"), "Correct-Horse-9!"));
        Assert.Equal(PasswordCheck.Success, current.Check(new IdentityPasswordService(50_000).Hash("Correct-Horse-9!"), "Correct-Horse-9!"));
    }

    [Fact]
    public void Upgrading_produces_a_hash_that_verifies_at_the_new_cost()
    {
        var current = new IdentityPasswordService(20_000);
        var upgraded = current.Hash("Correct-Horse-9!");

        Assert.Equal(20_000, IterationsOf(upgraded));
        Assert.Equal(PasswordCheck.Success, current.Check(upgraded, "Correct-Horse-9!"));
    }

    [Fact]
    public void A_malformed_hash_fails_instead_of_throwing()
    {
        var service = new IdentityPasswordService(1_000);

        Assert.Equal(PasswordCheck.Failed, service.Check("not-a-valid-hash", "x"));
        Assert.Equal(PasswordCheck.Failed, service.Check(string.Empty, "x"));
    }
}
