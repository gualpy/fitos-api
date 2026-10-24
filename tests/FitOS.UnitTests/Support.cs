using FitOS.Application.Auth;
using System.Security.Cryptography;
using FitOS.Application.Email;
using FitOS.Application.Secrets;
using FitOS.Application.Team;

namespace FitOS.UnitTests;

/// <summary>Test doubles shared by the onboarding, setup-link and platform tests.</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeEmail : IEmailSender
{
    public List<EmailMessage> Sent { get; } = [];

    public bool Fail { get; init; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new EmailDeliveryException("down");
        }

        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSetupStore : IPasswordSetupStore
{
    public Dictionary<string, (Guid UserId, DateTimeOffset Expires, bool Used, string Purpose, DateTimeOffset Created)> Tokens { get; } = [];

    public string? PasswordSet { get; private set; }

    public Task<bool> IssueAsync(Guid userId, byte[] tokenHash, DateTimeOffset expiresAt, string purpose, TimeSpan minGap, CancellationToken cancellationToken)
    {
        if (minGap > TimeSpan.Zero && Tokens.Values.Any(t => t.UserId == userId && t.Purpose == purpose && !t.Used && t.Created > Now - minGap))
        {
            return Task.FromResult(false);
        }

        foreach (var key in Tokens.Where(t => t.Value.UserId == userId && !t.Value.Used).Select(t => t.Key).ToList())
        {
            Tokens.Remove(key);
        }

        Tokens[Convert.ToHexString(tokenHash)] = (userId, expiresAt, false, purpose, Now);
        return Task.FromResult(true);
    }

    public Task<SetupRedeem> RedeemAsync(byte[] tokenHash, string passwordHash, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(tokenHash);
        if (!Tokens.TryGetValue(key, out var token) || token.Used)
        {
            return Task.FromResult(new SetupRedeem(SetupRedeemStatus.Invalid));
        }

        if (token.Expires <= Now)
        {
            return Task.FromResult(new SetupRedeem(SetupRedeemStatus.Expired));
        }

        Tokens[key] = token with { Used = true };
        PasswordSet = passwordHash;
        return Task.FromResult(new SetupRedeem(SetupRedeemStatus.Ok, token.UserId, null));
    }

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public Task<SetupInfo?> InfoAsync(byte[] tokenHash, CancellationToken cancellationToken)
    {
        var valid = Tokens.TryGetValue(Convert.ToHexString(tokenHash), out var token) && !token.Used && token.Expires > Now;
        return Task.FromResult(valid ? new SetupInfo("Dueño", "Gimnasio Titan", "titan", token.Purpose) : null);
    }
}

internal sealed class FakeSecretProtector : ISecretProtector
{
    public bool Broken { get; set; }

    public byte[] Protect(string plaintext, Guid tenantId) => System.Text.Encoding.UTF8.GetBytes($"enc:{tenantId}:{plaintext}");

    public string Unprotect(byte[] protectedData, Guid tenantId)
    {
        var text = System.Text.Encoding.UTF8.GetString(protectedData);
        var prefix = $"enc:{tenantId}:";
        if (Broken || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("cannot decrypt");
        }

        return text[prefix.Length..];
    }
}

internal sealed class FakeEmailSettingsStore : IEmailSettingsStore
{
    public StoredEmailSettings? Stored { get; set; }

    public Guid? SavedBy { get; private set; }

    public Task<StoredEmailSettings?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task SaveAsync(string host, int port, SmtpSecurity security, string username, string fromAddress, byte[]? passwordEnc, string? passwordHint, Guid actorId, CancellationToken cancellationToken)
    {
        Stored = new StoredEmailSettings(host, port, security, username, fromAddress, passwordEnc ?? Stored?.PasswordEnc, passwordEnc is null ? Stored?.PasswordHint : passwordHint, DateTimeOffset.UtcNow);
        SavedBy = actorId;
        return Task.CompletedTask;
    }
}

internal sealed class FakeSmtp : ISmtpDelivery
{
    public List<(SmtpSettings Settings, EmailMessage Message)> Sent { get; } = [];

    /// <summary>Hosts that refuse the message with this reason.</summary>
    public Dictionary<string, string> FailHosts { get; } = [];

    public Task SendAsync(SmtpSettings settings, EmailMessage message, CancellationToken cancellationToken)
    {
        if (FailHosts.TryGetValue(settings.Host, out var reason))
        {
            throw new EmailDeliveryException("refused", reason);
        }

        Sent.Add((settings, message));
        return Task.CompletedTask;
    }
}

internal sealed class FakeLog : ISecurityLog
{
    public List<SecurityEvent> Events { get; } = [];

    public Task WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken)
    {
        Events.Add(securityEvent);
        return Task.CompletedTask;
    }
}

internal sealed class FakeChallengeStore : ILoginChallengeStore
{
    private (Guid UserId, string Link, string Secret, DateTimeOffset Expires, bool Used)? _challenge;

    public string? Email { get; init; } = "admin@platform.test";

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public bool Stored => _challenge is not null;

    public Task<string?> CreateAsync(Guid userId, byte[] linkHash, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (Email is null)
        {
            return Task.FromResult<string?>(null);
        }

        _challenge = (userId, Convert.ToHexString(linkHash), Convert.ToHexString(secretHash), expiresAt, false);
        return Task.FromResult<string?>(Email);
    }

    public Task<ChallengeRedeem> RedeemAsync(byte[] linkHash, byte[] secretHash, CancellationToken cancellationToken)
    {
        if (_challenge is not { } c || c.Used || c.Link != Convert.ToHexString(linkHash))
        {
            return Task.FromResult(new ChallengeRedeem(ChallengeRedeemStatus.Invalid));
        }

        _challenge = c with { Used = true }; // a try burns the challenge, right secret or not
        if (c.Secret != Convert.ToHexString(secretHash))
        {
            return Task.FromResult(new ChallengeRedeem(ChallengeRedeemStatus.Invalid));
        }

        return Task.FromResult(c.Expires <= Now ? new ChallengeRedeem(ChallengeRedeemStatus.Expired) : new ChallengeRedeem(ChallengeRedeemStatus.Ok, c.UserId));
    }
}

internal sealed class FakeUserStore : IUserStore
{
    public List<TeamMember> Staff { get; } = [];

    public List<NewUser> Created { get; } = [];

    public HashSet<string> Usernames { get; } = [];

    public HashSet<string> Emails { get; } = [];

    public GymIdentity Gym { get; set; } = new("Gimnasio Titan", "titan");

    public CustomerForAccess? Customer { get; set; }

    public CustomerUser? CustomerAccount { get; set; }

    public int ActiveOwners { get; set; } = 1;

    public List<string> Calls { get; } = [];

    public Task<IReadOnlyList<TeamMember>> ListStaffAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TeamMember>>(Staff);

    public Task<TeamMember?> FindStaffAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Staff.FirstOrDefault(m => m.Id == id));

    public Task<bool> UsernameExistsAsync(string username, CancellationToken cancellationToken) => Task.FromResult(Usernames.Contains(username));

    public Task<Guid> CreateAsync(NewUser user, CancellationToken cancellationToken)
    {
        if (!Emails.Add(user.Email.ToLowerInvariant()))
        {
            throw new FitOS.Application.Common.ConflictException("email_taken");
        }

        Usernames.Add(user.Username);
        Created.Add(user);
        return Task.FromResult(Guid.NewGuid());
    }

    public Task SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken)
    {
        Calls.Add($"active:{active}");
        return Task.CompletedTask;
    }

    public Task SetRoleAsync(Guid id, string role, CancellationToken cancellationToken)
    {
        Calls.Add($"role:{role}");
        return Task.CompletedTask;
    }

    public Task<int> CountActiveOwnersAsync(CancellationToken cancellationToken) => Task.FromResult(ActiveOwners);

    public Task RevokeSessionsAsync(Guid userId, string reason, CancellationToken cancellationToken)
    {
        Calls.Add($"revoke:{reason}");
        return Task.CompletedTask;
    }

    public Task<GymIdentity> GymAsync(CancellationToken cancellationToken) => Task.FromResult(Gym);

    public Task<CustomerForAccess?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken) => Task.FromResult(Customer);

    public Task<CustomerUser?> FindCustomerUserAsync(Guid customerId, CancellationToken cancellationToken) => Task.FromResult(CustomerAccount);
}

/// <summary>Runs the work at once, so a test can look at its effects.</summary>
internal sealed class ImmediateBackground(Func<Type, object> resolve) : IBackgroundWork
{
    public int Enqueued { get; private set; }

    public void Enqueue<TService>(Func<TService, CancellationToken, Task> work)
        where TService : notnull
    {
        Enqueued++;
        work((TService)resolve(typeof(TService)), CancellationToken.None).GetAwaiter().GetResult();
    }
}

internal sealed class FakeResetStore : IPasswordResetStore
{
    public ResetTarget? Target { get; set; }

    public string? AskedSlug { get; private set; }

    public string? AskedIdentifier { get; private set; }

    public Task<ResetTarget?> FindAsync(string tenantSlug, string identifier, CancellationToken cancellationToken)
    {
        AskedSlug = tenantSlug;
        AskedIdentifier = identifier;
        return Task.FromResult(Target);
    }
}
