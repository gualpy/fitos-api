namespace FitOS.Application.Auth;

public sealed record RefreshResult(RefreshStatus Status, LoginResult? Login = null)
{
    public bool Succeeded => Status == RefreshStatus.Ok && Login is { Succeeded: true };
}

/// <summary>
/// Keeps a login alive without asking for the password again, and ends it for real.
/// Access tokens are short; the renewal token rotates on every use, so a stolen copy stops working the moment the
/// legitimate client renews (and presenting an old one revokes the session).
/// </summary>
public sealed class SessionService(ISessionStore sessions, IAccessTokenIssuer tokens, ISecurityLog log)
{
    public async Task<RefreshResult> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken cancellationToken)
    {
        var outcome = await sessions.RotateAsync(refreshToken, cancellationToken);
        if (outcome.Status == RefreshStatus.Reuse)
        {
            // An already-used token came back: it was copied. The session is dead; the owner should hear about it.
            await log.WriteAsync(new SecurityEvent("refresh_token_reuse", UserId: outcome.UserId, Client: client), cancellationToken);
        }

        if (outcome.Status != RefreshStatus.Ok || outcome.User is null || outcome.SessionId is not { } sessionId || outcome.Refresh is null)
        {
            return new RefreshResult(outcome.Status == RefreshStatus.Ok ? RefreshStatus.Invalid : outcome.Status);
        }

        return new RefreshResult(
            RefreshStatus.Ok,
            new LoginResult(tokens.Issue(outcome.User, sessionId), LoginUser.From(outcome.User), outcome.Refresh));
    }

    public async Task LogoutAsync(Guid sessionId, Guid? userId, ClientInfo client, CancellationToken cancellationToken)
    {
        await sessions.RevokeAsync(sessionId, "logout", cancellationToken);
        await log.WriteAsync(new SecurityEvent("logout", UserId: userId, Client: client), cancellationToken);
    }
}
