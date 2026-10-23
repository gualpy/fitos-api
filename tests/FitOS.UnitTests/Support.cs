using FitOS.Application.Auth;
using System.Security.Cryptography;
using FitOS.Application.Email;
using FitOS.Application.Secrets;

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
    public Dictionary<string, (Guid UserId, DateTimeOffset Expires, bool Used)> Tokens { get; } = [];

    public string? PasswordSet { get; private set; }

    public Task IssueAsync(Guid userId, byte[] tokenHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        foreach (var key in Tokens.Where(t => t.Value.UserId == userId && !t.Value.Used).Select(t => t.Key).ToList())
        {
            Tokens.Remove(key);
        }

        Tokens[Convert.ToHexString(tokenHash)] = (userId, expiresAt, false);
        return Task.CompletedTask;
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
        return Task.FromResult(valid ? new SetupInfo("Dueño", "Gimnasio Titan", "titan") : null);
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
