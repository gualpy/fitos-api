namespace FitOS.Application.Auth;

public enum ChangePasswordOutcome
{
    Changed,
    WrongCurrentPassword,
    WeakPassword,
}

/// <summary>Change of one's own password. Never reveals why a weak password is weak beyond the outcome.</summary>
public sealed class ChangePasswordService(IPasswordStore store, IPasswordService passwords)
{
    public const int MinLength = 10;

    public const int MaxLength = 200;

    public async Task<ChangePasswordOutcome> ChangeAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var hash = await store.GetHashAsync(userId, cancellationToken);
        if (hash is null || !passwords.Verify(hash, currentPassword))
        {
            return ChangePasswordOutcome.WrongCurrentPassword;
        }

        if (!IsAcceptable(newPassword) || newPassword == currentPassword)
        {
            return ChangePasswordOutcome.WeakPassword;
        }

        await store.SetAsync(userId, passwords.Hash(newPassword), cancellationToken);
        return ChangePasswordOutcome.Changed;
    }

    public static bool IsAcceptable(string password) =>
        password.Length is >= MinLength and <= MaxLength
        && password.Any(char.IsLetter)
        && password.Any(c => !char.IsLetter(c));
}
