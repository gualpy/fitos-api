using FitOS.Application.Auth;
using FitOS.Domain.Tenancy;

namespace FitOS.UnitTests.Sessions;

public sealed class SessionServiceTests
{
    private sealed class Issuer : IAccessTokenIssuer
    {
        public List<(Guid UserId, Guid SessionId)> Issued { get; } = [];

        public AccessToken Issue(LoginRecord user, Guid sessionId)
        {
            Issued.Add((user.UserId, sessionId));
            return new AccessToken("access-" + sessionId, DateTimeOffset.UnixEpoch);
        }
    }

    private sealed class Log : ISecurityLog
    {
        public List<SecurityEvent> Events { get; } = [];

        public Task WriteAsync(SecurityEvent securityEvent, CancellationToken cancellationToken)
        {
            Events.Add(securityEvent);
            return Task.CompletedTask;
        }
    }

    private static readonly ClientInfo Client = new("203.0.113.5", "agent");

    private sealed class Store(RefreshOutcome outcome) : ISessionStore
    {
        public List<(Guid Session, string Reason)> Revoked { get; } = [];

        public string? Presented { get; private set; }

        public Task<(Guid SessionId, RefreshGrant Refresh)> CreateAsync(Guid userId, string? userAgent, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RefreshOutcome> RotateAsync(string presentedRefreshToken, CancellationToken cancellationToken)
        {
            Presented = presentedRefreshToken;
            return Task.FromResult(outcome);
        }

        public Task RevokeAsync(Guid sessionId, string reason, CancellationToken cancellationToken)
        {
            Revoked.Add((sessionId, reason));
            return Task.CompletedTask;
        }
    }

    private static readonly LoginRecord Ana = new(Guid.NewGuid(), Guid.NewGuid(), string.Empty, UserRole.Admin, "Ana", null, true, false, "active", true);

    [Fact]
    public async Task A_good_renewal_issues_a_new_access_token_for_the_same_session_and_the_rotated_refresh_token()
    {
        var session = Guid.NewGuid();
        var grant = new RefreshGrant("next-token", DateTimeOffset.UnixEpoch.AddHours(8));
        var issuer = new Issuer();
        var service = new SessionService(new Store(new RefreshOutcome(RefreshStatus.Ok, Ana, session, grant)), issuer, new Log());

        var result = await service.RefreshAsync("old-token", Client, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("access-" + session, result.Login!.Token!.Value);
        Assert.Equal(grant, result.Login.Refresh);
        Assert.Equal(Ana.UserId, result.Login.User!.Id);
        Assert.True(result.Login.User.MustChangePassword); // a pending password change survives renewal
        Assert.Equal([(Ana.UserId, session)], issuer.Issued);
    }

    [Theory]
    [InlineData(RefreshStatus.Invalid)]
    [InlineData(RefreshStatus.Expired)]
    [InlineData(RefreshStatus.Reuse)]
    [InlineData(RefreshStatus.Concurrent)]
    [InlineData(RefreshStatus.Inactive)]
    public async Task Any_failure_issues_nothing_and_keeps_its_status(RefreshStatus status)
    {
        var issuer = new Issuer();
        var service = new SessionService(new Store(new RefreshOutcome(status)), issuer, new Log());

        var result = await service.RefreshAsync("whatever", Client, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(status, result.Status);
        Assert.Null(result.Login);
        Assert.Empty(issuer.Issued);
    }

    [Fact]
    public async Task An_incomplete_ok_outcome_is_never_trusted()
    {
        var service = new SessionService(new Store(new RefreshOutcome(RefreshStatus.Ok)), new Issuer(), new Log());

        var result = await service.RefreshAsync("x", Client, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(RefreshStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Logout_revokes_exactly_the_given_session_with_the_logout_reason()
    {
        var store = new Store(new RefreshOutcome(RefreshStatus.Invalid));
        var session = Guid.NewGuid();
        var user = Guid.NewGuid();
        var log = new Log();

        await new SessionService(store, new Issuer(), log).LogoutAsync(session, user, Client, CancellationToken.None);

        Assert.Equal([(session, "logout")], store.Revoked);
        var logged = Assert.Single(log.Events);
        Assert.Equal("logout", logged.Name);
        Assert.Equal(user, logged.UserId);
    }

    [Fact]
    public async Task A_replayed_token_is_logged_against_its_owner_as_probable_theft()
    {
        var owner = Guid.NewGuid();
        var log = new Log();
        var service = new SessionService(new Store(new RefreshOutcome(RefreshStatus.Reuse, UserId: owner)), new Issuer(), log);

        await service.RefreshAsync("old", Client, CancellationToken.None);

        var logged = Assert.Single(log.Events);
        Assert.Equal("refresh_token_reuse", logged.Name);
        Assert.Equal(owner, logged.UserId);
        Assert.Equal(Client, logged.Client);
    }

    [Theory]
    [InlineData(RefreshStatus.Invalid)]
    [InlineData(RefreshStatus.Expired)]
    [InlineData(RefreshStatus.Concurrent)]
    [InlineData(RefreshStatus.Inactive)]
    public async Task Only_a_replay_is_worth_a_log_entry_not_ordinary_expiry(RefreshStatus status)
    {
        var log = new Log();

        await new SessionService(new Store(new RefreshOutcome(status)), new Issuer(), log).RefreshAsync("t", Client, CancellationToken.None);

        Assert.Empty(log.Events);
    }
}
