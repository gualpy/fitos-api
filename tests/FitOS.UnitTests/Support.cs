using FitOS.Application.Auth;
using FitOS.Application.Email;

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
